# AnonymousBot

Telegram-бот на ASP.NET Core Minimal API с Webhook, EF Core и PostgreSQL.

## Требования

- .NET 8 SDK
- PostgreSQL 12 или новее, доступный по строке подключения
- Telegram bot token и публичный HTTPS-адрес приложения
- Доступ в интернет для обращения к Telegram Bot API

## Конфигурация и запуск

Для локального запуска заполните шаблонные значения в игнорируемом Git файле `appsettings.json` или задайте параметры переменными окружения. Не публикуйте файл с реальными секретами:

```powershell
$env:BOT_TOKEN = "<telegram-bot-token>"
$env:BOT_USERNAME = "<bot-username-without-at-sign>"
$env:WEBHOOK_URL = "https://your-public-domain.example/telegram/webhook"
$env:WEBHOOK_SECRET = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5432;Database=anonymousbot;Username=anonymousbot;Password=<password>"
dotnet run --project .\AnonymousBot.csproj
```

### Локальная проверка webhook через ngrok

1. Установите ngrok и настройте authtoken по инструкции в панели ngrok.
2. Запустите туннель на локальный порт приложения:

   ```powershell
   ngrok http 5023
   ```

3. Скопируйте HTTPS Forwarding URL из вывода ngrok, например `https://abc123.ngrok-free.app`, и добавьте к нему `/telegram/webhook`. Укажите полный адрес в поле `WEBHOOK_URL`, например `https://abc123.ngrok-free.app/telegram/webhook`.
4. Заполните `Telegram:BotToken`, `Telegram:BotUsername`, PostgreSQL connection string и `WEBHOOK_SECRET` в `appsettings.json`, затем запустите приложение с HTTP-профилем:

   ```powershell
   dotnet run --project .\AnonymousBot.csproj --launch-profile http
   ```

5. Оставьте оба процесса запущенными, проверьте `https://abc123.ngrok-free.app/health` и отправьте `/start` боту.

На бесплатном ngrok URL может измениться после перезапуска туннеля. В этом случае обновите `WEBHOOK_URL` и перезапустите приложение, иначе Telegram продолжит отправлять обновления на прежний адрес.

Приложение применяет PostgreSQL migrations при запуске. Для ручного применения/создания схемы используйте:

```powershell
dotnet ef database update --context PostgreSqlAppDbContext
```

PostgreSQL-схема находится в `Data\PostgreSqlMigrations`. Существующие SQL Server migrations в `Data\Migrations` сохранены для истории и не применяются к PostgreSQL; пакет SQL Server EF provider оставлен только для компиляции метаданных этих старых migrations. Не запускайте их против PostgreSQL.

`WEBHOOK_URL` должен быть полным публичным HTTPS URL с путём `/telegram/webhook`. `WEBHOOK_SECRET` должен содержать 1–256 символов из `A-Z`, `a-z`, `0-9`, `_` и `-`. Установите в хостинге те же переменные окружения; перенаправьте указанный HTTPS-маршрут на приложение. В production отсутствие или неверный формат секрета приводит к отказу запуска.

`WEBHOOK_SECRET` — это случайный пароль для проверки подлинности входящих webhook-запросов: приложение перед обработкой Update сравнивает его с заголовком `X-Telegram-Bot-Api-Secret-Token`, который Telegram отправляет после регистрации webhook. Придумайте/сгенерируйте его сами; получить его у BotFather нельзя. В PowerShell сгенерируйте подходящее значение командой:

```powershell
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
```

Скопируйте результат в `WEBHOOK_SECRET` и храните его в секрете или локальном `appsettings.json`, не публикуя и не отправляя другим. Значение должно оставаться тем же, пока приложение регистрирует webhook с этим секретом.

Приложение применяет EF Core migrations, запускает HTTP сервер и один раз при старте регистрирует Webhook в Telegram. Webhook не удаляется при остановке приложения. При ошибке регистрации запуск завершается с безопасным сообщением без вывода токена или секрета. Проверка состояния: `GET /health`.

Webhook-запросы принимаются только POST на `/telegram/webhook`. До чтения JSON проверяется заголовок `X-Telegram-Bot-Api-Secret-Token`. Обновления с некорректным секретом отклоняются. При успешной обработке endpoint отвечает HTTP 200; ошибка обработки возвращает HTTP 500, чтобы Telegram мог повторить доставку. Строка подключения задаётся через `ConnectionStrings__DefaultConnection`; формат Npgsql: `Host=<host>;Port=5432;Database=<database>;Username=<user>;Password=<password>;SSL Mode=Require`. Для добавления миграций используйте `dotnet ef migrations add <MigrationName> --context PostgreSqlAppDbContext --output-dir Data\PostgreSqlMigrations`. В Cloud Run задайте эту переменную и Telegram-переменные как runtime environment variables/secrets. Для Cloud SQL используйте подключённый Cloud SQL instance и соответствующие параметры соединения/SSL из конфигурации Cloud Run; реальные секреты храните в Secret Manager, а не в образе приложения.

Для локальной разработки Telegram не сможет доставить обновления на `localhost`: настройте публичный HTTPS endpoint и укажите его в `WEBHOOK_URL`.

Команда `/start` регистрирует пользователя по Telegram ID, сохраняет имя и username и отвечает `Добро пожаловать!`. Для заблокированных пользователей бот сообщает, что доступ ограничен.

Команда `/link` отправляет зарегистрированному пользователю его постоянную персональную ссылку. Имя бота задаётся через `BOT_USERNAME` или обратно совместимый ключ `Telegram__BotUsername`.

При открытии персональной ссылки команда `/start <TOKEN>` проверяет владельца и создаёт или заменяет сессию отправителя.

Когда отправитель отвечает текстом в активной сессии, бот сохраняет анонимный вопрос и уведомляет владельца ссылки с кнопками действий. Отправительские данные в вопрос и уведомление не включаются. Лимит отправки — пять анонимных вопросов за десять минут на отправителя.

Обработанные `update_id` сохраняются в PostgreSQL с уникальным ключом. Конкурентная доставка одного update сериализуется PostgreSQL session-level advisory lock; повтор уже обработанного update подтверждается без повторного запуска обработчика. PostgreSQL integration tests запускаются командой `dotnet test` при наличии доступной тестовой базы в `POSTGRES_CONNECTION_STRING`; каждый тест создаёт и затем удаляет собственную схему в этой базе, поэтому для пользователя нужны права `CREATE SCHEMA` и `DROP SCHEMA`.

Дедупликация не делает транзакционными SQL изменения и отправку сообщений через Telegram API. Если процесс завершится после выполнения части бизнес-логики, но до сохранения `update_id`, Telegram может повторить update, и уже выполненные внешние эффекты могут повториться. Гарантия exactly-once delivery не заявляется.

Serilog в текущем проекте не настроен; приложение использует стандартный ASP.NET Core `ILogger`.