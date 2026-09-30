# AnonymousBot

Минимальный Telegram-бот на ASP.NET Core Minimal API. Бот получает обновления через long polling, сохраняет текстовые сообщения в SQL Server и отправляет ответ с эхом.

## Требования

- .NET 8 SDK
- SQL Server, доступный по строке подключения
- Telegram bot token от BotFather
- Доступ в интернет для обращения к Telegram Bot API

## Настройка

Bot token задавайте через переменную окружения. Публичный адрес и настройка webhook не нужны:

```powershell
$env:Telegram__BotToken = "<telegram-bot-token>"
$env:ConnectionStrings__DefaultConnection = "Server=localhost;Database=AnonymousBot;Trusted_Connection=True;TrustServerCertificate=True;"
dotnet run --project .\AnonymousBot.csproj
```

При первом запуске приложение создаёт базу и таблицу через `EnsureCreated`. Для последующих изменений схемы добавьте EF Core migrations.

При запуске приложение удаляет ранее установленный webhook и начинает получать сообщения через long polling. Оно должно оставаться запущенным. Проверка состояния приложения: `GET /health`.