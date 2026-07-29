"""
RivalVeil — Нагрузочный тест FastAPI сервера
Используется для дипломной работы: измеряет производительность сервера

Установка:  pip install aiohttp
Запуск:     python load_test.py
"""

import asyncio
import aiohttp
import time
import statistics
import random
import string

BASE_URL = "http://localhost:8000"


def random_name():
    return "bot_" + ''.join(random.choices(string.ascii_lowercase, k=8))


async def simulate_player(session: aiohttp.ClientSession, player_id: int) -> dict:
    """Симулирует одного игрока: регистрация → вход → лидерборд → статус сервера"""
    start = time.time()
    errors = []
    username = f"bot_{player_id}_{int(time.time() * 1000) % 100000}"

    try:
        # 1. Регистрация
        async with session.post(f"{BASE_URL}/auth/register", json={
            "username": username,
            "password": "test123456"
        }) as resp:
            if resp.status not in (200, 201, 400):
                errors.append(f"register: HTTP {resp.status}")

        # 2. Вход
        token = ""
        async with session.post(f"{BASE_URL}/auth/login", json={
            "username": username,
            "password": "test123456"
        }) as resp:
            if resp.status == 200:
                data = await resp.json()
                token = data.get("token", "")
            else:
                errors.append(f"login: HTTP {resp.status}")

        headers = {"Authorization": f"Bearer {token}"}

        # 3. Проверка профиля
        async with session.get(f"{BASE_URL}/players/me", headers=headers) as resp:
            if resp.status != 200:
                errors.append(f"profile: HTTP {resp.status}")

        # 4. Лидерборд
        async with session.get(f"{BASE_URL}/leaderboard", headers=headers) as resp:
            if resp.status != 200:
                errors.append(f"leaderboard: HTTP {resp.status}")

        # 5. Статус сервера
        async with session.get(f"{BASE_URL}/server/status") as resp:
            if resp.status != 200:
                errors.append(f"server_status: HTTP {resp.status}")

    except Exception as e:
        errors.append(f"exception: {str(e)[:50]}")

    elapsed = time.time() - start
    return {
        "time":      elapsed,
        "errors":    errors,
        "player_id": player_id
    }


async def run_load_test(concurrent_users: int) -> dict:
    print(f"\n{'═' * 55}")
    print(f"  Нагрузочный тест: {concurrent_users} одновременных пользователей")
    print(f"{'═' * 55}")

    connector = aiohttp.TCPConnector(limit=concurrent_users + 20)
    timeout   = aiohttp.ClientTimeout(total=30)

    async with aiohttp.ClientSession(connector=connector, timeout=timeout) as session:
        start_total = time.time()
        tasks   = [simulate_player(session, i) for i in range(concurrent_users)]
        results = await asyncio.gather(*tasks, return_exceptions=True)
        total   = time.time() - start_total

    times      = [r["time"]   for r in results if isinstance(r, dict)]
    errors     = [r["errors"] for r in results if isinstance(r, dict) and r["errors"]]
    exceptions = [r           for r in results if isinstance(r, Exception)]

    summary = {
        "users":      concurrent_users,
        "success":    len(times),
        "errors":     len(errors) + len(exceptions),
        "total_time": total,
        "avg_ms":     statistics.mean(times)   * 1000 if times else 0,
        "max_ms":     max(times)               * 1000 if times else 0,
        "min_ms":     min(times)               * 1000 if times else 0,
        "median_ms":  statistics.median(times) * 1000 if times else 0,
    }

    status = "✅" if summary["errors"] == 0 else "⚠️"
    print(f"  {status} Успешных:        {summary['success']}/{concurrent_users}")
    print(f"  ❌ Ошибок:          {summary['errors']}")
    print(f"  🕐 Общее время:     {summary['total_time']:.2f} сек")
    print(f"  ⚡ Среднее:         {summary['avg_ms']:.0f} мс")
    print(f"  📊 Медиана:         {summary['median_ms']:.0f} мс")
    print(f"  🔺 Максимум:        {summary['max_ms']:.0f} мс")
    print(f"  🔻 Минимум:         {summary['min_ms']:.0f} мс")

    return summary


async def main():
    print("╔═══════════════════════════════════════════════════════╗")
    print("║           RivalVeil — Нагрузочный тест               ║")
    print("║      Данные для дипломной работы                      ║")
    print("╚═══════════════════════════════════════════════════════╝")
    print(f"\nСервер: {BASE_URL}")

    # Проверяем доступность сервера
    try:
        async with aiohttp.ClientSession() as s:
            async with s.get(f"{BASE_URL}/health",
                             timeout=aiohttp.ClientTimeout(total=5)) as r:
                if r.status == 200:
                    data = await r.json()
                    print(f"✅ Сервер доступен | БД: {data.get('db_connected')} | "
                          f"Игровой сервер: {data.get('server_online')}")
                else:
                    print(f"❌ Сервер вернул HTTP {r.status}")
                    return
    except Exception as e:
        print(f"❌ Нет подключения: {e}")
        return

    all_results = []

    # Запускаем тесты с нарастающей нагрузкой
    for users in [10, 50, 100, 200]:
        result = await run_load_test(users)
        all_results.append(result)
        await asyncio.sleep(3)

    # Итоговая таблица для диплома
    print(f"\n{'═' * 65}")
    print("  ИТОГОВАЯ ТАБЛИЦА ДЛЯ ДИПЛОМНОЙ РАБОТЫ")
    print(f"{'═' * 65}")
    print(f"  {'Пользователей':>15} {'Успешных':>12} {'Среднее мс':>12} "
          f"{'Медиана мс':>12} {'Макс мс':>10}")
    print(f"  {'-' * 60}")
    for r in all_results:
        ok = "✅" if r["errors"] == 0 else "⚠️"
        print(f"  {r['users']:>14} {ok} "
              f"{r['success']:>7}/{r['users']} "
              f"{r['avg_ms']:>12.0f} "
              f"{r['median_ms']:>12.0f} "
              f"{r['max_ms']:>10.0f}")
    print(f"{'═' * 65}")
    print("\n  Скопируй эту таблицу в дипломную работу!")


if __name__ == "__main__":
    asyncio.run(main())
