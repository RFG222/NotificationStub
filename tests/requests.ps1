param(
    # Адрес запущенного приложения. При необходимости передайте другой через -BaseUrl.
    [string]$BaseUrl = "http://localhost:5292"
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd("/")
$script:passed = 0
$script:failed = 0

function Write-TestResult {
    param(
        [string]$Name,
        [bool]$Success,
        [string]$Details = ""
    )

    if ($Success) {
        Write-Host "[PASS] $Name $Details" -ForegroundColor Green
        $script:passed++
    }
    else {
        Write-Host "[FAIL] $Name $Details" -ForegroundColor Red
        $script:failed++
    }
}

# Преобразует JSON-ответ в массив записей.
# Поддерживает как JSON-массив, так и один JSON-объект.
function Convert-ResponseJson {
    param(
        [AllowEmptyString()]
        [string]$Content
    )

    if ([string]::IsNullOrWhiteSpace($Content)) {
        return
    }

    $parsed = ConvertFrom-Json -InputObject $Content

    if ($null -eq $parsed) {
        return
    }

    if ($parsed -is [System.Array]) {
        foreach ($item in $parsed) {
            Write-Output -NoEnumerate $item
        }
    }
    else {
        Write-Output -NoEnumerate $parsed
    }
}

# Отправляет HTTP-запрос и выводит запрос с ответом сервера.
function Invoke-LoggedRequest {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [ValidateSet("GET", "POST", "DELETE")]
        [string]$Method,

        [Parameter(Mandatory)]
        [string]$Path,

        [string]$Body
    )

    $uri = "$BaseUrl$Path"

    Write-Host ""
    Write-Host ">>> ЗАПРОС: $Name" -ForegroundColor Cyan
    Write-Host "$Method $uri" -ForegroundColor DarkCyan

    $requestParams = @{
        Method = $Method
        Uri    = $uri
    }

    if ($PSBoundParameters.ContainsKey("Body")) {
        Write-Host "Тело запроса:" -ForegroundColor Gray
        Write-Host $Body

        $requestParams.ContentType = "application/json"
        $requestParams.Body = $Body
    }

    try {
        # -UseBasicParsing убирает предупреждение в Windows PowerShell 5.1.
        $response = Invoke-WebRequest @requestParams -UseBasicParsing

        Write-Host "<<< ОТВЕТ СЕРВЕРА: HTTP $([int]$response.StatusCode)" `
            -ForegroundColor Green

        if (-not [string]::IsNullOrWhiteSpace($response.Content)) {
            Write-Host "Тело ответа:" -ForegroundColor Gray

            try {
                $json = ConvertFrom-Json -InputObject $response.Content
                $pretty = ConvertTo-Json -InputObject $json -Depth 20
                Write-Host $pretty
            }
            catch {
                Write-Host $response.Content
            }
        }
        else {
            Write-Host "(пустое тело ответа)"
        }

        return $response
    }
    catch {
        Write-Host "<<< ОШИБКА HTTP-ЗАПРОСА: $($_.Exception.Message)" `
            -ForegroundColor Red
        throw
    }
}

Write-Host ""
Write-Host "Тестирование NotificationStub: $BaseUrl" -ForegroundColor Cyan
Write-Host "Скрипт отправляет реальные HTTP-запросы к приложению."
Write-Host "В конце тестирования хранилище будет очищено."
Write-Host ""

# 1. Проверка доступности API
try {
    $null = Invoke-LoggedRequest `
        -Name "Проверка доступности API" `
        -Method GET `
        -Path "/api/requests"

    Write-TestResult -Name "API доступен" -Success $true
}
catch {
    Write-TestResult -Name "API доступен" `
        -Success $false `
        -Details $_.Exception.Message

    Write-Host "Остановлено: запустите приложение и повторите тест." `
        -ForegroundColor Yellow
    exit 1
}

# 2. Отправка двух уведомлений
try {
    $paymentBody = @{
        event  = "payment"
        amount = 1500
        status = "success"
    } | ConvertTo-Json

    $refundBody = @{
        event  = "refund"
        amount = 300
        status = "pending"
    } | ConvertTo-Json

    $null = Invoke-LoggedRequest `
        -Name "Добавление платежа" `
        -Method POST `
        -Path "/notifications/payment" `
        -Body $paymentBody

    $null = Invoke-LoggedRequest `
        -Name "Добавление возврата" `
        -Method POST `
        -Path "/notifications/refund" `
        -Body $refundBody

    Write-TestResult -Name "POST: отправка двух уведомлений" -Success $true
}
catch {
    Write-TestResult -Name "POST: отправка двух уведомлений" `
        -Success $false `
        -Details $_.Exception.Message
}

# 3. Проверка сохранения обеих записей
try {
    $response = Invoke-LoggedRequest `
        -Name "Получение списка записей" `
        -Method GET `
        -Path "/api/requests"

    $records = @(Convert-ResponseJson -Content $response.Content)

    $hasPayment = @(
        $records | Where-Object {
            $_.path -like "*/notifications/payment"
        }
    ).Count -ge 1

    $hasRefund = @(
        $records | Where-Object {
            $_.path -like "*/notifications/refund"
        }
    ).Count -ge 1

    Write-TestResult `
        -Name "GET: записи сохранены" `
        -Success ($hasPayment -and $hasRefund) `
        -Details "(в ответе записей: $($records.Count))"
}
catch {
    Write-TestResult -Name "GET: записи сохранены" `
        -Success $false `
        -Details $_.Exception.Message
}

# 4. Поиск по полю JSON body
try {
    $response = Invoke-LoggedRequest `
        -Name "Поиск по полю status=success" `
        -Method GET `
        -Path "/api/requests?field=status&value=success"

    $filtered = @(Convert-ResponseJson -Content $response.Content)

    $filterWorks = @(
        $filtered | Where-Object {
            $_.body.status -eq "success"
        }
    ).Count -ge 1

    Write-TestResult `
        -Name "Поиск по полю body: status=success" `
        -Success $filterWorks `
        -Details "(найдено записей: $($filtered.Count))"
}
catch {
    Write-TestResult -Name "Поиск по полю body: status=success" `
        -Success $false `
        -Details $_.Exception.Message
}

# 5. Поиск по пути запроса
try {
    $response = Invoke-LoggedRequest `
        -Name "Поиск по пути" `
        -Method GET `
        -Path "/api/requests?path=notifications/payment"

    $byPath = @(Convert-ResponseJson -Content $response.Content)

    $pathWorks = @(
        $byPath | Where-Object {
            $_.path -like "*/notifications/payment"
        }
    ).Count -ge 1

    Write-TestResult `
        -Name "Поиск по пути запроса" `
        -Success $pathWorks `
        -Details "(найдено записей: $($byPath.Count))"
}
catch {
    Write-TestResult -Name "Поиск по пути запроса" `
        -Success $false `
        -Details $_.Exception.Message
}

# 6. Проверка пагинации
try {
    $response = Invoke-LoggedRequest `
        -Name "Проверка пагинации" `
        -Method GET `
        -Path "/api/requests?offset=0&limit=1"

    $page = @(Convert-ResponseJson -Content $response.Content)

    Write-TestResult `
        -Name "Пагинация: offset=0, limit=1" `
        -Success ($page.Count -eq 1) `
        -Details "(получено записей: $($page.Count))"
}
catch {
    Write-TestResult -Name "Пагинация: offset=0, limit=1" `
        -Success $false `
        -Details $_.Exception.Message
}

# 7. Очистка хранилища и проверка результата
try {
    $deleteResponse = Invoke-LoggedRequest `
        -Name "Очистка хранилища" `
        -Method DELETE `
        -Path "/api/requests"

    $deleteResult = ConvertFrom-Json -InputObject $deleteResponse.Content
    $clearedCount = [int]$deleteResult.clearedCount

    $response = Invoke-LoggedRequest `
        -Name "Проверка после очистки" `
        -Method GET `
        -Path "/api/requests"

    $afterDelete = @(Convert-ResponseJson -Content $response.Content)
    $remainingCount = $afterDelete.Count

    Write-TestResult `
        -Name "DELETE: очистка хранилища" `
        -Success ($remainingCount -eq 0) `
        -Details "(удалено записей: $clearedCount; осталось: $remainingCount)"
}
catch {
    Write-TestResult -Name "DELETE: очистка хранилища" `
        -Success $false `
        -Details $_.Exception.Message
}

# Итог
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "ИТОГ: пройдено $script:passed, ошибок $script:failed." `
    -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($script:failed -gt 0) {
    exit 1
}

exit 0