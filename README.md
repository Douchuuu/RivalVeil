# RivalVeil

**RivalVeil** — многопользовательский соревновательный Action Roguelite на Unity с авторитетным сервером, детерминированной генерацией мира и рейтинговой системой ELO.

## Технологический стек

* **Game Client:** Unity 2022.3 LTS (URP)
* **Networking:** Netcode for GameObjects (NGO) + Unity Transport & Relay
* **Architecture:** VContainer (DI), Unity Jobs System + Burst Compiler
* **Backend:** Python 3.11 (FastAPI) + MySQL 8.0

## Основные фичи

* **1v1 Shadow Multiplayer:** Игроки сражаются в параллельных мирах с идентичной генерацией, видя лишь «призрачные» проекции друг друга.
* **Shared Seed Generation:** Единый ключ генерации гарантирует одинаковый ландшафт, спавн сундуков и баланс для обоих игроков.
* **Оптимизация роя:** Отрисовка и просчет 700+ активных врагов через Unity Jobs и Spatial Hashing.
* **Серверная валидация:** Защита от накрутки урона и убийств на стороне FastAPI бэкенда.
* **Рейтинговый подбор:** Автоматический поиск оппонентов на основе ELO-рейтинга.

##  Быстрый запуск

###  Запуск готовой сборки (Offline / Standalone)
Готовый билд клиента находится в репозитории:
`GameClient/RivalVeil.exe`
P.S. Для онлайн режима требуется сервер, про настройку сервера читать в README_SETUP.md
---

###  Развертывание для разработки

#### 1. Бэкенд (FastAPI + MySQL)

```bash
# Клонирование и переход в папку бэкенда
git clone [https://github.com/Douchuuu/RivalVeil.git](https://github.com/Douchuuu/RivalVeil.git)
cd RivalVeil/Backend

# Установка зависимостей
pip install -r requirements.txt

# Запуск API (перед запуском импортируйте database.sql в ваш MySQL)
uvicorn main:app --reload
