using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Читаем конфигурацию 
int maxStorageSize = builder.Configuration.GetValue<int>("MAX_STORAGE_SIZE", 0);
int cleanupIntervalSeconds = builder.Configuration.GetValue<int>("CLEANUP_INTERVAL_SECONDS", 600);
int retentionPeriodSeconds = builder.Configuration.GetValue<int>("RETENTION_PERIOD_SECONDS", 3600);
bool autoCleanupEnabled = builder.Configuration.GetValue<bool>("AUTO_CLEANUP_ENABLED", true);

// Регистрируем потокобезопасное хранилище в памяти
builder.Services.AddSingleton<InMemoryNotificationStorage>(sp => 
    new InMemoryNotificationStorage(maxStorageSize, sp.GetRequiredService<ILogger<InMemoryNotificationStorage>>()));

// Подключаем фоновую службу автоочистки по времени (если включена)[cite: 3]
if (autoCleanupEnabled)
{
    builder.Services.AddHostedService(sp => new CleanupBackgroundService(
        sp.GetRequiredService<InMemoryNotificationStorage>(),
        sp.GetRequiredService<ILogger<CleanupBackgroundService>>(),
        TimeSpan.FromSeconds(cleanupIntervalSeconds),
        TimeSpan.FromSeconds(retentionPeriodSeconds)
    ));
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();
app.MapControllers();

app.Run();

// --- МОДЕЛЬ ДАННЫХ ---
public record NotificationRecord(
    Guid Id,
    DateTime Timestamp,
    string Path,
    JsonElement Body
);

// --- ХРАНИЛИЩЕ В ПАМЯТИ ---
public class InMemoryNotificationStorage
{
    private readonly int _maxStorageSize;
    private readonly ILogger<InMemoryNotificationStorage> _logger;
    private readonly ConcurrentDictionary<Guid, NotificationRecord> _storage = new();
    private readonly ConcurrentQueue<Guid> _queue = new();

    public InMemoryNotificationStorage(int maxStorageSize, ILogger<InMemoryNotificationStorage> logger)
    {
        _maxStorageSize = maxStorageSize;
        _logger = logger;
    }

    public void Add(string path, JsonElement body)
    {
        var id = Guid.NewGuid();
        var record = new NotificationRecord(id, DateTime.UtcNow, path, body);

        // Ограничение максимального количества записей (FIFO вытеснение)[cite: 2, 3]
        if (_maxStorageSize > 0)
        {
            while (_storage.Count >= _maxStorageSize)
            {
                if (_queue.TryDequeue(out var oldestId))
                {
                    if (_storage.TryRemove(oldestId, out _))
                    {
                        _logger.LogInformation("Вытеснена старая запись из-за лимита MAX_STORAGE_SIZE: {Id}", oldestId);
                    }
                }
                else
                {
                    break;
                }
            }
        }

        _storage[id] = record;
        _queue.Enqueue(id);
    }

    public IEnumerable<NotificationRecord> Search(string? query, int offset, int limit)
    {
        var items = _storage.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(x => 
                x.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Body.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
            );
        }

        return items.OrderByDescending(x => x.Timestamp).Skip(offset).Take(limit);
    }

    public int Clear()
    {
        int count = _storage.Count;
        _storage.Clear();
        while (_queue.TryDequeue(out _)) { }
        _logger.LogInformation("Хранилище очищено. Удалено записей: {Count}", count);
        return count;
    }

    public int RemoveOlderThan(TimeSpan maxAge)
    {
        var threshold = DateTime.UtcNow - maxAge;
        int removedCount = 0;

        foreach (var pair in _storage)
        {
            if (pair.Value.Timestamp < threshold)
            {
                if (_storage.TryRemove(pair.Key, out _))
                {
                    removedCount++;
                }
            }
        }

        if (removedCount > 0)
        {
            _logger.LogInformation("Фоновая автоочистка удалила {Count} устаревших записей[cite: 3]", removedCount);
        }

        return removedCount;
    }
}

// --- ФОНОВАЯ СЛУЖБА ОЧИСТКИ ---
public class CleanupBackgroundService : BackgroundService
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
        _storage = storage;
        _logger = logger;
        _interval = interval;
        _retentionPeriod = retentionPeriod;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);
                _storage.RemoveOlderThan(_retentionPeriod);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в фоновой службе очистки");
            }
        }
    }
}

// --- КОНТРОЛЛЕР ---
[ApiController]
public class NotificationsController : ControllerBase
{
    private readonly InMemoryNotificationStorage _storage;

    public NotificationsController(InMemoryNotificationStorage storage)
    {
        _storage = storage;
    }

    // Прием любых входящих уведомлений (статус 200 OK)[cite: 1]
    [HttpPost("{*path}")]
    public IActionResult ReceiveNotification(string path, [FromBody] JsonElement body)
    {
        _storage.Add("/" + path, body);
        return Ok(new { status = "success", message = "Уведомление успешно принято" });
    }

    // Поиск сохраненных запросов[cite: 2, 3]
    [HttpGet("api/requests")]
    public IActionResult GetRequests([FromQuery] string? q, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
    {
        var result = _storage.Search(q, offset, limit);
        return Ok(result);
    }

    // Полная очистка хранилища[cite: 3]
    [HttpDelete("api/requests")]
    public IActionResult ClearStorage()
    {
        int removed = _storage.Clear();
        return Ok(new { status = "success", clearedCount = removed });
    }
}