# RivalVeil Server Setup Guide

## Быстрый старт

### 1. Настройка GitHub Gist

1. Создайте новый Gist: https://gist.github.com
   - **Filename**: `rivalveil_url.txt`
   - **Content**: `http://localhost:8000` (временно)
   - Можно сделать Public или Secret

2. Получите Gist ID из URL:
   ```
   https://gist.github.com/YOUR_USERNAME/GIST_ID
   ```

3. Создайте GitHub Token:
   - Перейдите: https://github.com/settings/tokens
   - Нажмите "Generate new token (classic)"
   - Выберите scope: `gist`
   - Сохраните токен!

### 2. Настройка файлов

#### url_updater.py
```python
GITHUB_USERNAME = "ваш_github_username"  # <-- ЗАМЕНИТЕ!
GIST_ID = "f33d3895db2ed6966728ad38f29bc83e"  # <-- ЗАМЕНИТЕ!
```

#### BackendService.cs (Unity)
```csharp
[SerializeField] private string gistRawUrl = 
    "https://gist.githubusercontent.com/ВАШ_USERNAME/GIST_ID/raw/rivalveil_url.txt";
```

#### .env
```bash
# Скопируйте из примера
copy .env.example .env

# Отредактируйте:
DB_PASSWORD=ваш_пароль_бд
GIST_TOKEN=ghp_ваш_токен
GAME_EXE=C:\Путь\К\RivalVeil.exe
```

### 3. Установка зависимостей

```bash
pip install -r requirements.txt
```

### 4. Запуск

```bash
start_all.bat
```

Или вручную:
```bash
# Терминал 1: FastAPI
uvicorn main:app --host 0.0.0.0 --port 8000

# Терминал 2: Cloudflare + URL Updater
python url_updater.py
```

## Как это работает

```
┌─────────────┐     ┌──────────────┐     ┌─────────────┐
│   Unity     │────▶│  GitHub Gist │◀────│ url_updater │
│   Client    │     │  (raw URL)   │     │   (Python)  │
└─────────────┘     └──────────────┘     └──────┬──────┘
                                                │
                                         ┌──────▼──────┐
                                         │  Cloudflare │
                                         │   Tunnel    │
                                         └──────┬──────┘
                                                │
                                         ┌──────▼──────┐
                                         │   FastAPI   │
                                         │   Server    │
                                         └─────────────┘
```

## Flow

1. **url_updater.py** запускает Cloudflare tunnel
2. Получает публичный URL (например, `https://abc123.trycloudflare.com`)
3. Записывает URL в GitHub Gist
4. **Unity клиент** при старте читает URL из Gist
5. Клиент подключается к вашему серверу!

## Безопасность

- ✅ Токен хранится в `.env` (не в коде)
- ✅ `.env` в `.gitignore` (не попадёт в git)
- ✅ Rate limiting на login/matchmaking
- ✅ bcrypt для паролей

## Troubleshooting

### "GIST_TOKEN not set"
```bash
# Windows
set GIST_TOKEN=ghp_xxx

# Или добавьте в .env
```

### "Failed to fetch URL from Gist" (Unity)
- Проверьте `gistRawUrl` в `BackendService.cs`
- Убедитесь, что Gist public
- Проверьте интернет-соединение

### "Too many login attempts"
- Rate limiting активирован
- Подождите 60 секунд

## Файлы

| Файл | Описание |
|------|----------|
| `.env` | Переменные окружения (не коммитить!) |
| `.env.example` | Шаблон для .env |
| `url_updater.py` | Обновляет URL в Gist |
| `BackendService.cs` | Unity клиент с Gist fetch |
| `main.py` | FastAPI сервер |
| `start_all.bat` | Запускает всё |
