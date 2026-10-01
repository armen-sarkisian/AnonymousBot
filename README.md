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
$env:Telegram__BotUsername = "<bot-username-without-at-sign>"
$env:ConnectionStrings__DefaultConnection = "Server=localhost;Database=AnonymousBot;Trusted_Connection=True;TrustServerCertificate=True;"
dotnet run --project .\AnonymousBot.csproj
```

При первом запуске приложение применяет EF Core migrations и создаёт нужные таблицы.

При запуске приложение удаляет ранее установленный webhook и начинает получать сообщения через long polling. Оно должно оставаться запущенным. Проверка состояния приложения: `GET /health`.

Схема базы управляется через EF Core migrations. Миграции применяются при старте приложения; строку подключения можно переопределить через `ConnectionStrings__DefaultConnection`. Для создания новой миграции используйте `dotnet ef migrations add <MigrationName>`.

Команда `/start` регистрирует пользователя по Telegram ID, сохраняет имя и username и отвечает `Добро пожаловать!`. Для заблокированных пользователей бот сообщает, что доступ ограничен.

Команда `/link` отправляет зарегистрированному пользователю его постоянную персональную ссылку. Имя бота задаётся через `Telegram:BotUsername` или переменную окружения `Telegram__BotUsername`.

При открытии персональной ссылки команда `/start <TOKEN>` проверяет владельца и создаёт или заменяет сессию отправителя. Следующее текстовое сообщение только завершает сессию и пока не сохраняется и не отправляется получателю.