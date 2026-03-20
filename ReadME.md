# WebOffice
<img width="1534" height="627" alt="image" src="https://github.com/user-attachments/assets/e1d4b8f5-fd14-47e6-a162-b07245e368a5" />


WebOffice это веб система для онлайн редактирования офисных документов с полной интеграцией OnlyOffice Document Server через протокол WOPI. Пользователи могут удобно создавать новые файлы, загружать существующие документы в форматах .docx, .xlsx, .pptx и других совместимых расширениях, а затем полноценно редактировать их прямо в браузере в знакомом и мощном интерфейсе OnlyOffice. Система обеспечивает надёжное сохранение всех изменений и стабильную работу с документами любого объёма.

Проект построен на актуальном стеке .NET 9: backend реализован на ASP.NET Core 9 с Entity Framework Core, frontend полностью на Blazor WebAssembly (без JavaScript), файлы хранятся в объектном хранилище MinIO. Всё легко запускается локально через Docker Compose одной командой. Достаточно скопировать .env, поднять инфраструктуру и запустить приложения через dotnet run. Подходит для личного использования, небольших команд, корпоративного документооборота или как готовая основа для собственных решений без внешних облачных сервисов и подписок.

# Инструкции по запуску

<details>

## Быстрый старт для разработки
1. Склонируйте репозиторий:
   ```bash
   git clone https://github.com/sisharppravi/WebOffice.git
2. Скопируйте файл `.env`

```env
# MinIO
MINIO_ROOT_USER=admin
MINIO_ROOT_PASSWORD=admin123
MINIO_BUCKET=weboffice-files

# OnlyOffice
ONLYOFFICE_JWT_SECRET=78YsTwvZAo646cK0BRZn2yJYps26Wx4M7sfnvzTd0nY=

# Nginx
NGINX_PORT=80
```

3. Запустите backend: `cd WebOffice.Api && dotnet run`

4. Запустите frontend: `cd ../WebOffice.Client && dotnet run`

5. Запустите инфраструктуру (MinIO + OnlyOffice + Nginx): `docker compose up`

Откройте в браузере `http://localhost`

Зарегистрируйте нового пользователя и войдите в систему
<img src="images/img.png" alt="Регистрация">
<img src="images/img_1.png" alt="Вход">

Создайте новый документ и отредактируйте его, чтобы убедиться, что интеграция с OnlyOffice работает корректно
<img src="images/img_2.png" alt="Создание документа">
<img src="images/img_3.png" alt="Редактирование в OnlyOffice">
</details>

# Использованные технологии

<details>

## Backend (WebOffice.Api)

| Технология| Версия |
|---|---|
| .NET / ASP.NET Core | 9.0 |
| Entity Framework Core | 9.0.14 |
| Microsoft.EntityFrameworkCore.Sqlite | 9.0.14 |
| Microsoft.AspNetCore.Identity | 2.3.9 |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 9.0.14 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 9.0.3 |
| Microsoft.IdentityModel.Tokens | 8.16.0 |
| System.IdentityModel.Tokens.Jwt | 8.16.0 |
| Microsoft.AspNetCore.OpenApi | 9.0.8 |
| Swashbuckle.AspNetCore (Swagger) | 9.0.6 |
| Minio (.NET SDK) | 7.0.0 |

## Frontend (WebOffice.Client)

| Технология |Версия |
|---|---|
| .NET / Blazor WebAssembly | 9.0 |
| Microsoft.AspNetCore.Components.WebAssembly | 9.0.8 |

## Docker инфраструктура

| Образ | Версия |
|---|---|
| MinIO | RELEASE.2024-02-17T01-15-57Z |
| OnlyOffice Document Server | 8.0.1 |
| Nginx | 1.27.0 |

Перед началом работы выполните команду `dotnet ef database update`

Для работы со Swagger перейдите в браузере по адресу `https://localhost:7130/swagger`

</details>


# WOPI (OnlyOffice) интеграция и отладка

Проект использует OnlyOffice Document Server через протокол WOPI для встраивания редактора документов в браузер. Ниже краткая инструкция по настройке, распространённые ошибки и советы по их устранению.

## Переменные окружения (важно)

Убедитесь, что в окружении (или в `.env`) заданы, как минимум, эти переменные:

```env
# MinIO
MINIO_ROOT_USER=admin
MINIO_ROOT_PASSWORD=admin123
MINIO_BUCKET=weboffice-files

# OnlyOffice / WOPI
ONLYOFFICE_JWT_SECRET=78YsTwvZAo646cK0BRZn2yJYps26Wx4M7sfnvzTd0nY=
ONLYOFFICE_WOPI_ENABLED=true
ONLYOFFICE_WOPI_HOST=http://onlyoffice:8080       # адрес Document Server (в Docker/compose)
ONLYOFFICE_WOPI_EXTERNAL_URL=http://localhost     # origin, который видит браузер (parentOrigin)

# Nginx (если используется)
NGINX_PORT=80
```

- `ONLYOFFICE_JWT_SECRET` должен совпадать с настройкой JWT в самом Document Server. Несовпадение токенов частая причина ошибок доступа.
- `ONLYOFFICE_WOPI_HOST` адрес Document Server внутри Docker сети. Внешний URL для iframe/редактора задаёт `ONLYOFFICE_WOPI_EXTERNAL_URL`.

## Nginx / прокси

Если вы используете nginx как обратный прокси, добавьте корректную проксировку и проброс заголовков JWT/Authorization. Пример конфигурации (вставьте в ваш серверный блок):

```
location /wopi/ {
    proxy_pass http://onlyoffice:80/; 
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Authorization $http_authorization;
    proxy_buffering off;
}
```

Настройте имена сервисов и порты под вашу конфигурацию Docker Compose.

## Service Worker и ошибка "Failed to fetch"

Если в консоли вы видите ошибки наподобие:

- document_editor_service_worker.js:23  Uncaught (in promise) TypeError: Failed to fetch
- The FetchEvent for "http://localhost:8080/.../index.html?..." resulted in a network error response: the promise was rejected.

это часто происходит потому, что service worker фронтенда перехватывает запросы к внешнему OnlyOffice (другой origin) и пытается их кэшировать или обработать. Варианты решений:

1. Игнорировать внешние URL в fetch-обработчике service worker (рекомендуется для разработки). Пример проверки:

```javascript
// В document_editor_service_worker.js
if (event.request.url.includes('/wopi') || new URL(event.request.url).origin !== self.location.origin) {
  // не обрабатывать запросы к WOPI и внешним origin
  return;
}
```

2. Удалить/зарегистрировать заново service worker в браузере: откройте DevTools -> Application -> Service Workers -> Unregister, затем очистите кэш и перезагрузите страницу.

3. Убедитесь, что Document Server доступен по указанному адресу и не блокируется брандмауэром/прокси.

## Ошибка: "Вы пытаетесь выполнить действие, на которое у вас нет прав"

Эта ошибка приходит от Document Server и означает, что WOPI-запрос (или JWT) не соответствует ожиданиям сервера. Проверьте:

- Совпадает ли `ONLYOFFICE_JWT_SECRET` между проектом и Document Server.
- Правильно ли backend формирует WOPI-метаданные (доступы: readonly/edit) и возвращает корректный WOPI URL и токен.
- CORS / `parentOrigin`: убедитесь, что Origin, с которого открывается редактор (обычно `http://localhost`), добавлен/разрешён Document Server или прокси.
- Логи OnlyOffice Document Server там обычно видно точную причину отказа (некорректный токен, неверный URL, недостаточно прав).

Если проблема возникает только у вас (у других работает), проверьте локальные различия:

- Service worker / кэш браузера (см. выше).
- Файлы hosts (C:\Windows\System32\drivers\etc\hosts) нет ли локального перенаправления.
- Локальный брандмауэр или антивирус, блокирующие порт 8080.
- Разные версии браузера/расширения, блокирующие запросы.

## StorageInitializer / Database/MinIO почему "горит" красным в IDE

Если в IDE (Rider/Visual Studio) класс `StorageInitializer`/`DatabaseInitializer` подсвечивается красным или вы видите ошибки при запуске:

- Проверьте, зарегистрирован ли соответствующий сервис в DI в `Program.cs` и вызывается ли инициализация при старте.
- Убедитесь, что для работы StorageInitializer доступны значения конфигурации (MinIO endpoint, credentials). Отсутствие переменных окружения может приводить к ошибкам во время анализа/рантайма.
- Запустите миграции (`dotnet ef database update`) и проверьте, что база данных доступна.
- Проверьте логи приложения при старте большинство ошибок и исключений там видны.

Пример типичных действий при проблемах со StorageInitializer:

1. Убедиться, что MinIO запущен (docker compose up) и доступен по URL/порту.
2. Проверить настройки в `appsettings.json`/`appsettings.Development.json` и `.env`.
3. Запустить приложение и посмотреть вывод в консоли если есть исключение при инициализации, оно даст конкретную подсказку.

## Быстрая отладка и чек-лист

1. Скопируйте `.env` из примера выше и перезапустите docker compose.
2. Убедитесь, что OnlyOffice отвечает: откройте в браузере `http://localhost:8080` (или адрес из `ONLYOFFICE_WOPI_HOST`).
3. Сравните `ONLYOFFICE_JWT_SECRET` с настройками Document Server.
4. Удалите service worker и кэш браузера (DevTools -> Application -> Service Workers -> Unregister).
5. Посмотрите логи backend и OnlyOffice при попытке открыть документ ищите ошибки 401/403 или сообщения про invalid token.
6. Если локально у вас ошибка, а у других нет проверьте hosts, брандмауэр и расширения браузера.

## Команды (PowerShell)

```powershell
# поднять инфраструктуру
docker compose up -d

# запустить backend
cd WebOffice.Api; dotnet run

# запустить frontend
cd ../WebOffice.Client; dotnet run

# обновить БД
cd WebOffice.Api; dotnet ef database update
```


# Что добавить/проверить в проекте (рекомендации для разработчиков)

- Игнорировать WOPI/OnlyOffice URL в service worker по умолчанию, или иметь отдельный dev режим, в котором SW отключён.
- Логировать точные WOPI-запросы и ответы (включая заголовки Authorization) в режиме разработки это решает большинство проблем с правами доступа.
- Добавить секцию в README с инструкцией по снятию/повторной регистрации Service Worker для разработчиков.
