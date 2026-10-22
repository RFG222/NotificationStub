
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

// ============================================================
// 1. НАСТРОЙКА ПРИЛОЖЕНИЯ
// ============================================================

var builder = WebApplication.CreateBuilder(args);

// Читаем настройки из appsettings.json или переменных окружения.
int maxStorageSize =
    builder.Configuration.GetValue<int>("MAX_STORAGE_SIZE", 0);

int cleanupIntervalSeconds =
    builder.Configuration.GetValue<int>("CLEANUP_INTERVAL_SECONDS", 600);

int retentionPeriodSeconds =
    builder.Configuration.GetValue<int>("RETENTION_PERIOD_SECONDS", 3600);

bool autoCleanupEnabled =
    builder.Configuration.GetValue<bool>("AUTO_CLEANUP_ENABLED", true);

// Проверяем корректность конфигурации при запуске.
if (maxStorageSize < 0)
{
    throw new InvalidOperationException(
        "MAX_STORAGE_SIZE должен быть равен 0 или больше.");
}

if (cleanupIntervalSeconds <= 0)
{
    throw new InvalidOperationException(
        "CLEANUP_INTERVAL_SECONDS должен быть больше 0.");
}

if (retentionPeriodSeconds <= 0)
{
    throw new InvalidOperationException(
        "RETENTION_PERIOD_SECONDS должен быть больше 0.");
}

// Регистрируем единый экземпляр потокобезопасного хранилища.
builder.Services.AddSingleton<InMemoryNotificationStorage>(services =>
    new InMemoryNotificationStorage(
        maxStorageSize,
        services.GetRequiredService<
            ILogger<InMemoryNotificationStorage>>()));

// Подключаем фоновую очистку, если она включена настройкой.
if (autoCleanupEnabled)
{
    builder.Services.AddHostedService<CleanupBackgroundService>(services =>
        new CleanupBackgroundService(
            services.GetRequiredService<InMemoryNotificationStorage>(),
            services.GetRequiredService<
                ILogger<CleanupBackgroundService>>(),
            TimeSpan.FromSeconds(cleanupIntervalSeconds),
            TimeSpan.FromSeconds(retentionPeriodSeconds)));
}

// Подключаем контроллеры и Swagger.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Глобальная обработка необработанных исключений.
app.UseMiddleware<ExceptionMiddleware>();

// Документация API доступна по адресу /swagger.
app.UseSwagger();
app.UseSwaggerUI();

// Регистрируем маршруты контроллеров.
app.MapControllers();

app.Run();


// ============================================================
// 2. МОДЕЛЬ СОХРАНЯЕМОГО УВЕДОМЛЕНИЯ
// ============================================================

// Каждая запись содержит уникальный ID, время получения,
// исходный путь запроса и JSON-тело уведомления.
public sealed record NotificationRecord(
    Guid Id,
    DateTime Timestamp,
    string Path,
    JsonElement Body);


// ============================================================
// 3. ПОТОКОБЕЗОПАСНОЕ ХРАНИЛИЩЕ В ПАМЯТИ
// ============================================================

public sealed class InMemoryNotificationStorage
{
    // Один замок защищает словарь и порядок записей вместе.
    // Благодаря этому лимит не нарушается при параллельном добавлении.
    private readonly object _sync = new();

    private readonly Dictionary<Guid, NotificationRecord> _records = new();

    // Связанный список хранит порядок поступления записей:
    // первая запись в списке является самой старой.
    private readonly LinkedList<Guid> _insertionOrder = new();

    private readonly int _maxStorageSize;
    private readonly ILogger<InMemoryNotificationStorage> _logger;

    public InMemoryNotificationStorage(
        int maxStorageSize,
        ILogger<InMemoryNotificationStorage> logger)
    {
        if (maxStorageSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxStorageSize),
                "Размер хранилища не может быть отрицательным.");
        }

        _maxStorageSize = maxStorageSize;
        _logger = logger;
    }

    // Добавляет уведомление и при необходимости вытесняет старейшие записи.
    public void Add(string path, JsonElement body)
    {
        lock (_sync)
        {
            // Значение 0 означает отсутствие ограничения.
            // Если лимит достигнут, удаляем записи по принципу FIFO.
            while (_maxStorageSize > 0 &&
                   _records.Count >= _maxStorageSize)
            {
                var oldestNode = _insertionOrder.First;

                if (oldestNode is null)
                {
                    break;
                }

                Guid oldestId = oldestNode.Value;

                _records.Remove(oldestId);
                _insertionOrder.RemoveFirst();

                _logger.LogInformation(
                    "Удалена самая старая запись {Id}: достигнут лимит хранения.",
                    oldestId);
            }

            Guid id = Guid.NewGuid();

            // Clone позволяет безопасно хранить JSON после завершения
            // обработки исходного HTTP-запроса.
            var record = new NotificationRecord(
                id,
                DateTime.UtcNow,
                path,
                body.Clone());

            _records.Add(id, record);
            _insertionOrder.AddLast(id);

            _logger.LogInformation(
                "Получено уведомление {Id} по пути {Path}.",
                id,
                path);
        }
    }

    // Возвращает записи с фильтрами и пагинацией.
    public IReadOnlyList<NotificationRecord> Search(
        string? query,
        string? path,
        string? field,
        string? value,
        int offset,
        int limit)
    {
        NotificationRecord[] snapshot;

        // Создаём согласованный снимок записей под блокировкой.
        // Фильтрацию выполняем уже за пределами блокировки.
        lock (_sync)
        {
            snapshot = _records.Values.ToArray();
        }

        IEnumerable<NotificationRecord> results = snapshot;

        // Общий поиск по ID, пути, именам JSON-полей и значениям.
        if (!string.IsNullOrWhiteSpace(query))
        {
            results = results.Where(record =>
                record.Id.ToString().Contains(
                    query, StringComparison.OrdinalIgnoreCase)
                ||
                record.Path.Contains(
                    query, StringComparison.OrdinalIgnoreCase)
                ||
                JsonContains(record.Body, query));
        }

        // Отдельный фильтр по пути запроса.
        if (!string.IsNullOrWhiteSpace(path))
        {
            results = results.Where(record =>
                record.Path.Contains(
                    path, StringComparison.OrdinalIgnoreCase));
        }

        // Фильтр по конкретному полю JSON и, если задано, его значению.
        if (!string.IsNullOrWhiteSpace(field))
        {
            results = results.Where(record =>
                JsonFieldMatches(record.Body, field, value));
        }
        else if (!string.IsNullOrWhiteSpace(value))
        {
            // Если имя поля не задано, ищем значение среди всех полей.
            results = results.Where(record =>
                JsonContains(record.Body, value));
        }

        // Сначала новые записи, затем применение пагинации.
        return results
            .OrderByDescending(record => record.Timestamp)
            .ThenBy(record => record.Id)
            .Skip(offset)
            .Take(limit)
            .ToArray();
    }

    // Рекурсивно ищет текст в именах полей и значениях JSON.
    private static bool JsonContains(JsonElement element, string query)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains(
                        query, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (JsonContains(property.Value, query))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (JsonContains(item, query))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.String:
                return (element.GetString() ?? "").Contains(
                    query, StringComparison.OrdinalIgnoreCase);

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.GetRawText().Contains(
                    query, StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }

    // Ищет поле с указанным именем в том числе во вложенных объектах.
    private static bool JsonFieldMatches(
        JsonElement element,
        string field,
        string? value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(
                    field, StringComparison.OrdinalIgnoreCase))
                {
                    // Если значение не задано, достаточно наличия поля.
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        return true;
                    }

                    // Если задано значение, оно должно находиться
                    // в значении найденного поля.
                    if (JsonContains(property.Value, value))
                    {
                        return true;
                    }
                }

                // Продолжаем поиск во вложенных объектах.
                if (JsonFieldMatches(property.Value, field, value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (JsonFieldMatches(item, field, value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Удаляет все записи из хранилища.
    public int Clear()
    {
        lock (_sync)
        {
            int count = _records.Count;

            _records.Clear();
            _insertionOrder.Clear();

            _logger.LogInformation(
                "Хранилище полностью очищено. Удалено записей: {Count}.",
                count);

            return count;
        }
    }

    // Удаляет записи, которые старше заданного срока хранения.
    public int RemoveOlderThan(TimeSpan retentionPeriod)
    {
        DateTime threshold = DateTime.UtcNow - retentionPeriod;
        int removedCount = 0;

        lock (_sync)
        {
            var node = _insertionOrder.First;

            while (node is not null)
            {
                var next = node.Next;

                if (_records.TryGetValue(node.Value, out var record) &&
                    record.Timestamp < threshold)
                {
                    _records.Remove(node.Value);
                    _insertionOrder.Remove(node);
                    removedCount++;
                }

                node = next;
            }
        }

        if (removedCount > 0)
        {
            _logger.LogInformation(
                "Фоновая очистка удалила устаревших записей: {Count}.",
                removedCount);
        }

        return removedCount;
    }

    // Возвращает текущее количество записей.
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _records.Count;
            }
        }
    }
}


// ============================================================
// 4. ФОНОВАЯ СЛУЖБА АВТООЧИСТКИ
// ============================================================

public sealed class CleanupBackgroundService : BackgroundService
{
    private readonly InMemoryNotificationStorage _storage;
    private readonly ILogger<CleanupBackgroundService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _retentionPeriod;

    public CleanupBackgroundService(
        InMemoryNotificationStorage storage,
        ILogger<CleanupBackgroundService> logger,
        TimeSpan interval,
        TimeSpan retentionPeriod)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                "Интервал очистки должен быть больше нуля.");
        }

        if (retentionPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionPeriod),
                "Срок хранения должен быть больше нуля.");
        }

        _storage = storage;
        _logger = logger;
        _interval = interval;
        _retentionPeriod = retentionPeriod;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Служба автоочистки запущена. Интервал: {Interval}; срок хранения: {Retention}.",
            _interval,
            _retentionPeriod);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ждём заданный интервал перед очередной очисткой.
                await Task.Delay(_interval, stoppingToken);

                _storage.RemoveOlderThan(_retentionPeriod);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // Нормальное завершение фоновой службы.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Ошибка во время фоновой очистки.");
            }
        }

        _logger.LogInformation("Служба автоочистки остановлена.");
    }
}


// ============================================================
// 5. ГЛОБАЛЬНАЯ ОБРАБОТКА ИСКЛЮЧЕНИЙ
// ============================================================

public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(
        RequestDelegate next,
        ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Необработанная ошибка при обработке {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);

            // Если сервер уже начал отправлять ответ, изменить его нельзя.
            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode =
                StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(new
            {
                status = "error",
                message = "Внутренняя ошибка сервера."
            });
        }
    }
}


// ============================================================
// 6. КОНТРОЛЛЕР ПРИЁМА И ПРОСМОТРА УВЕДОМЛЕНИЙ
// ============================================================

[ApiController]
public sealed class NotificationsController : ControllerBase
{
    private readonly InMemoryNotificationStorage _storage;

    public NotificationsController(InMemoryNotificationStorage storage)
    {
        _storage = storage;
    }

    // Принимает JSON-уведомления по произвольным POST-путям.
    [HttpPost("{*path}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ReceiveNotification(
        [FromBody] JsonElement body)
    {
        // Сохраняем фактический путь без дополнительного слеша.
        string path = Request.Path.Value ?? "/";

        _storage.Add(path, body);

        // Возвращаем HTTP 200 для успешно принятого уведомления.
        return Ok(new
        {
            status = "success",
            message = "Уведомление успешно принято"
        });
    }

    // Возвращает сохранённые записи с фильтрами и пагинацией.
    // Примеры: /api/requests?q=payment
    //          /api/requests?path=payment
    //          /api/requests?field=status&value=success
    //          /api/requests?offset=0&limit=20
    [HttpGet("api/requests")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult GetRequests(
        [FromQuery] string? q,
        [FromQuery] string? path,
        [FromQuery] string? field,
        [FromQuery] string? value,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50)
    {
        // Не допускаем отрицательное смещение и слишком большой лимит.
        if (offset < 0)
        {
            return BadRequest(new
            {
                error = "Параметр offset не может быть отрицательным."
            });
        }

        if (limit < 1 || limit > 1000)
        {
            return BadRequest(new
            {
                error = "Параметр limit должен быть от 1 до 1000."
            });
        }

        var records = _storage.Search(
            q,
            path,
            field,
            value,
            offset,
            limit);

        return Ok(records);
    }

    // Удаляет все записи и возвращает количество удалённых записей.
    [HttpDelete("api/requests")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ClearStorage()
    {
        int removed = _storage.Clear();

        return Ok(new
        {
            status = "success",
            clearedCount = removed
        });
    }
}
