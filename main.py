"""
RivalVeil — FastAPI v5.1 + rooms_v2 fix

ИСПРАВЛЕНИЯ rooms_v2:
  [NEW]   Приватные комнаты для игры с другом (Play with Friend) без ELO.
  [NEW]   POST /rooms/create      — хост создаёт комнату (с created_at).
  [NEW]   POST /rooms/{code}/relay — хост сохраняет Relay join_code после создания аллокации.
  [NEW]   GET  /room/find         — гость поллит комнату; возвращает has_relay + join_code.
  [NEW]   DELETE /rooms/{code}    — хост закрывает комнату (только свою).
  [NEW]   is_room_match в MatchResultRequest — если true, ELO не меняется.
  [NEW]   _startup_cleanup() — очистка: пустые комнаты >5 мин, любые >20 мин.

ИСПРАВЛЕНИЯ v5.1:
  [CRITICAL] _try_create_match(player, db) — вынесен из /matchmaking/join.
             Вызывается в трёх точках: join, status, server/register.

ИСПРАВЛЕНИЯ v5.0:
  [CRITICAL] MySQLConnectionPool, SELECT FOR UPDATE, server_id проверка.
  [HIGH]     ON DUPLICATE KEY UPDATE для live_match_weapons/totems.
  [MEDIUM]   _startup_cleanup использует created_at в game_sessions.
"""

from dotenv import load_dotenv
load_dotenv()

import os
import datetime
import random
import string
import secrets
import time
import subprocess
import mysql.connector
from mysql.connector.pooling import MySQLConnectionPool
from mysql.connector import Error
from fastapi import FastAPI, HTTPException, Depends, Request
import bcrypt
from pydantic import BaseModel, validator

# --- RATE LIMITING -----------------------------------------------------------
_login_attempts = {}
_matchmaking_attempts = {}
RATE_LIMIT_WINDOW = 60
MAX_LOGIN_ATTEMPTS = 5
MAX_MATCHMAKING_JOINS = 10

def _check_rate_limit(ip: str, attempts_dict: dict, max_attempts: int) -> bool:
    now = time.monotonic()
    if ip in attempts_dict:
        attempts_dict[ip] = [(t, c) for t, c in attempts_dict[ip] if now - t < RATE_LIMIT_WINDOW]
    count = sum(c for t, c in attempts_dict.get(ip, []))
    if count >= max_attempts:
        return False
    if ip not in attempts_dict:
        attempts_dict[ip] = []
    attempts_dict[ip].append((now, 1))
    return True

def _get_client_ip(request: Request) -> str:
    forwarded = request.headers.get("X-Forwarded-For")
    if forwarded:
        return forwarded.split(",")[0].strip()
    return request.client.host if request.client else "unknown"


# ═══ НАСТРОЙКИ ════════════════════════════════════════════════════════════════

DB_HOST     = os.getenv("DB_HOST",     "localhost")
DB_USER     = os.getenv("DB_USER",     "root")
DB_PASSWORD = os.getenv("DB_PASSWORD", "")
DB_NAME     = os.getenv("DB_NAME",     "rivalveil")
DB_PORT     = int(os.getenv("DB_PORT", "3306"))

_GAME_EXE_PATH = os.getenv("GAME_EXE_PATH", r"C:\RivalVeilServer\GameServer\RivalVeil.exe")
_SERVER_LOG_DIR = os.getenv("SERVER_LOG_DIR", r"C:\RivalVeilServer\logs")

FASTAPI_SECRET    = os.getenv("FASTAPI_SECRET",    "change-me")
SERVER_API_SECRET = os.getenv("SERVER_API_SECRET", "server-secret")

# --- LOGGING -----------------------------------------------------------------
import logging
logging.basicConfig(
    level=logging.DEBUG,
    format="%(asctime)s | %(levelname)-8s | %(message)s",
    datefmt="%H:%M:%S"
)
logger = logging.getLogger("rv")

if FASTAPI_SECRET == "change-me":
    logger.warning("⚠️  FASTAPI_SECRET использует дефолтное значение 'change-me'!")
if SERVER_API_SECRET == "server-secret":
    logger.warning("⚠️  SERVER_API_SECRET использует дефолтное значение 'server-secret'!")

# --- FASTAPI APP --------------------------------------------------------------
app = FastAPI(title="RivalVeil Backend v5.1+rooms", version="5.1.1")

# --- DB POOL -----------------------------------------------------------------
_db_pool = None

def _get_db_pool():
    global _db_pool
    if _db_pool is None:
        try:
            _db_pool = MySQLConnectionPool(
                pool_name="rv_pool",
                pool_size=10,
                pool_reset_session=True,
                host=DB_HOST,
                user=DB_USER,
                password=DB_PASSWORD,
                database=DB_NAME,
                port=DB_PORT,
                autocommit=False
            )
            logger.info("DB pool created (MySQLConnectionPool, size=10)")
        except Error as e:
            logger.error(f"DB pool failed: {e}")
            raise HTTPException(status_code=503, detail="Database unavailable")
    return _db_pool

def get_db():
    db = None
    try:
        pool = _get_db_pool()
        db = pool.get_connection()
        yield db
    except Error as e:
        logger.error(f"DB connection error: {e}")
        raise HTTPException(status_code=503, detail="Database connection error")
    finally:
        if db is not None:
            db.close()

# --- PID TRACKER -------------------------------------------------------------
_spawned_server_pids = {}
_last_server_spawn = 0

# ═══════════════════════════════════════════════════════════════════════════════
# STARTUP CLEANUP
# ═══════════════════════════════════════════════════════════════════════════════

def _startup_cleanup():
    """
    ИСПРАВЛЕНО rooms_v2 fix:
      Разделено на два независимых соединения:
        1. Основная очистка (sessions, servers) — всегда работает.
        2. Очистка комнат — в отдельном соединении с отдельным try/except.

    ПРИЧИНА БАГА: раньше все 5 запросов шли в одном try-блоке.
      Если запросы к rooms (guest_id IS NULL, created_at) падали из-за
      отсутствия колонок (до миграции) — основная очистка тоже не
      коммитилась. Соединение возвращалось в пул в dirty-состоянии.
      Следующий pool.get_connection() получал это битое соединение → краш.
    """

    # ── Блок 1: основная очистка (сессии, серверы) ───────────────────────────
    db1 = None
    try:
        pool = _get_db_pool()
        db1 = pool.get_connection()
        cursor = db1.cursor()

        # 1. Сессии на мёртвых серверах → abandoned
        cursor.execute("""
            UPDATE game_sessions gs
            JOIN game_servers srv ON gs.server_id = srv.id
            SET gs.status = 'abandoned'
            WHERE gs.status IN ('waiting', 'active')
              AND TIMESTAMPDIFF(SECOND, srv.last_ping, NOW()) > 120
        """)
        dead_sessions = cursor.rowcount

        # 2. Сессии waiting без join_code > 90 сек → abandoned
        cursor.execute("""
            UPDATE game_sessions
            SET status = 'abandoned'
            WHERE status = 'waiting'
              AND join_code IS NULL
              AND TIMESTAMPDIFF(SECOND, created_at, NOW()) > 90
        """)
        stale_waiting = cursor.rowcount

        # 3. Удаляем мёртвые серверы
        cursor.execute("""
            DELETE FROM game_servers
            WHERE TIMESTAMPDIFF(SECOND, last_ping, NOW()) > 300
        """)
        dead_servers = cursor.rowcount

        db1.commit()  # Коммитим основную очистку отдельно — безопасно
        logger.info(
            f"Startup cleanup [main]: dead_sessions={dead_sessions}, "
            f"stale_waiting={stale_waiting}, dead_servers={dead_servers}"
        )
    except Exception as e:
        logger.warning(f"Startup cleanup [main] error: {e}")
        try:
            if db1: db1.rollback()
        except Exception:
            pass
    finally:
        if db1 is not None:
            db1.close()

    # ── Блок 2: очистка комнат — отдельное соединение ─────────────────────────
    # Может упасть если migration_rooms_v1.sql ещё не применена (нет guest_id,
    # created_at). Это нормально — логируем и продолжаем работу.
    db2 = None
    try:
        pool = _get_db_pool()
        db2 = pool.get_connection()
        cursor2 = db2.cursor()

        # 4. Удаляем пустые комнаты (нет гостя) старше 5 минут
        cursor2.execute("""
            DELETE FROM rooms
            WHERE is_open = 1
              AND guest_id IS NULL
              AND created_at IS NOT NULL
              AND TIMESTAMPDIFF(MINUTE, created_at, NOW()) > 5
        """)
        expired_empty = cursor2.rowcount

        # 5. Закрываем любые открытые комнаты старше 20 минут
        cursor2.execute("""
            UPDATE rooms SET is_open = 0
            WHERE is_open = 1
              AND created_at IS NOT NULL
              AND TIMESTAMPDIFF(MINUTE, created_at, NOW()) > 20
        """)
        expired_old = cursor2.rowcount

        db2.commit()
        logger.info(
            f"Startup cleanup [rooms]: expired_empty={expired_empty}, "
            f"expired_old={expired_old}"
        )
    except Exception as e:
        # Скорее всего migration_rooms_v1.sql ещё не применена — не критично
        logger.debug(f"Startup cleanup [rooms] skipped (run migration_rooms_v1.sql): {e}")
        try:
            if db2: db2.rollback()
        except Exception:
            pass
    finally:
        if db2 is not None:
            db2.close()

_startup_cleanup()

# ═══════════════════════════════════════════════════════════════════════════════
# PYDANTIC MODELS
# ═══════════════════════════════════════════════════════════════════════════════

class AuthRequest(BaseModel):
    username: str
    password: str

    @validator("username")
    def validate_username(cls, v):
        if len(v) < 3 or len(v) > 32:
            raise ValueError("Username must be 3-32 characters")
        if not v.replace("_", "").replace("-", "").isalnum():
            raise ValueError("Username: alphanumeric, underscore, hyphen only")
        return v

    @validator("password")
    def validate_password(cls, v):
        if len(v) < 6 or len(v) > 128:
            raise ValueError("Password must be 6-128 characters")
        return v

class MatchResultRequest(BaseModel):
    winner_name:  str
    loser_name:   str
    winner_kills: int
    loser_kills:  int
    winner_level: int  = 1
    loser_level:  int  = 1
    duration_sec: int
    is_technical: bool = False
    is_room_match: bool = False   # [rooms_v2] если True — ELO не изменяется


# ═══════════════════════════════════════════════════════════════════════════════
# LIVE MATCH DATA — Pydantic модели
# ═══════════════════════════════════════════════════════════════════════════════

class LiveWeaponData(BaseModel):
    slot_index: int
    weapon_id: str
    weapon_name: str
    weapon_level: int

class LiveTotemData(BaseModel):
    slot_index: int
    totem_id: str
    totem_name: str
    bonus_type: str
    totem_level: int
    totem_value: float

class LiveItemData(BaseModel):
    item_id: str
    item_name: str
    item_rarity: str
    quantity: int = 1

class LivePlayerSnapshot(BaseModel):
    client_id: int
    username: str
    character_index: int
    level: int
    kills: int
    health: float
    max_health: float
    shield: float
    weapons: list[LiveWeaponData] = []
    totems: list[LiveTotemData] = []
    items: list[LiveItemData] = []

class LiveMatchUpdateRequest(BaseModel):
    session_id: int
    server_id: str
    match_time_seconds: int
    players: list[LivePlayerSnapshot]

# ═══════════════════════════════════════════════════════════════════════════════
# СЛУЖЕБНЫЕ
# ═══════════════════════════════════════════════════════════════════════════════

@app.get("/")
def root():
    return {"info": "RivalVeil Backend v5.1+rooms", "docs": "/docs", "db": DB_HOST}

@app.get("/health")
def health(db=Depends(get_db)):
    cursor = db.cursor(dictionary=True)
    cursor.execute("SELECT COUNT(*) AS cnt FROM players")
    players = cursor.fetchone()["cnt"]
    cursor.execute("SELECT COUNT(*) AS cnt FROM game_servers WHERE status='idle'")
    idle = cursor.fetchone()["cnt"]
    cursor.execute("SELECT COUNT(*) AS cnt FROM matchmaking_queue")
    queue = cursor.fetchone()["cnt"]
    cursor.execute("SELECT COUNT(*) AS cnt FROM rooms WHERE is_open=1")
    open_rooms = cursor.fetchone()["cnt"]
    return {
        "status": "ok",
        "players": players,
        "idle_servers": idle,
        "queue_size": queue,
        "open_rooms": open_rooms,
        "time": datetime.datetime.now().isoformat(),
    }


# ═══════════════════════════════════════════════════════════════════════════════
# АВТОРИЗАЦИЯ
# ═══════════════════════════════════════════════════════════════════════════════

@app.post("/auth/register")
def auth_register(data: AuthRequest, request: Request, db=Depends(get_db)):
    ip = _get_client_ip(request)
    if not _check_rate_limit(ip, _login_attempts, MAX_LOGIN_ATTEMPTS):
        raise HTTPException(429, "Too many login attempts")

    cursor = db.cursor(dictionary=True)
    cursor.execute("SELECT id FROM players WHERE username=%s", (data.username,))
    if cursor.fetchone():
        raise HTTPException(400, "Username already taken")

    hashed = bcrypt.hashpw(data.password.encode(), bcrypt.gensalt()).decode()
    cursor.execute("INSERT INTO players (username, password_hash) VALUES (%s, %s)",
                   (data.username, hashed))
    db.commit()
    return {"success": True, "player_id": cursor.lastrowid}

@app.post("/auth/login")
def auth_login(data: AuthRequest, request: Request, db=Depends(get_db)):
    ip = _get_client_ip(request)
    if not _check_rate_limit(ip, _login_attempts, MAX_LOGIN_ATTEMPTS):
        raise HTTPException(429, "Too many login attempts")

    cursor = db.cursor(dictionary=True)
    cursor.execute(
        "SELECT id, password_hash, username, rating, wins, losses FROM players WHERE username=%s",
        (data.username,)
    )
    row = cursor.fetchone()
    if not row or not bcrypt.checkpw(data.password.encode(), row["password_hash"].encode()):
        raise HTTPException(401, "Invalid credentials")

    token = secrets.token_hex(32)
    expires = datetime.datetime.now() + datetime.timedelta(hours=24)
    cursor.execute("""
        INSERT INTO sessions (player_id, token, expires_at)
        VALUES (%s, %s, %s)
        ON DUPLICATE KEY UPDATE token = VALUES(token), expires_at = VALUES(expires_at)
    """, (row["id"], token, expires))
    db.commit()
    return {
        "success": True,
        "token": token,
        "player_id": row["id"],
        "username": row["username"],
        "rating": row["rating"],
        "wins": row["wins"],
        "losses": row["losses"]
    }

@app.post("/auth/logout")
def auth_logout(request: Request, db=Depends(get_db)):
    token = request.headers.get("Authorization", "").replace("Bearer ", "")
    cursor = db.cursor()
    cursor.execute("DELETE FROM sessions WHERE token=%s", (token,))
    db.commit()
    return {"success": True}

def _require_auth(request: Request, db) -> dict:
    token = request.headers.get("Authorization", "").replace("Bearer ", "")
    if not token:
        raise HTTPException(401, "Missing token")
    cursor = db.cursor(dictionary=True)
    cursor.execute("SELECT * FROM sessions WHERE token=%s AND expires_at > NOW()", (token,))
    session = cursor.fetchone()
    if not session:
        raise HTTPException(401, "Invalid or expired token")
    cursor.execute("SELECT * FROM players WHERE id=%s", (session["player_id"],))
    return cursor.fetchone()

# ═══════════════════════════════════════════════════════════════════════════════
# ПРОФИЛЬ
# ═══════════════════════════════════════════════════════════════════════════════

@app.get("/players/me")
def get_me(request: Request, db=Depends(get_db)):
    player = _require_auth(request, db)
    return {"player": player}

@app.get("/players/{player_id}/history")
def get_history(player_id: int, limit: int = 20, db=Depends(get_db)):
    cursor = db.cursor(dictionary=True)
    cursor.execute("""
        SELECT * FROM matches
        WHERE player1_id=%s OR player2_id=%s
        ORDER BY played_at DESC LIMIT %s
    """, (player_id, player_id, min(limit, 100)))
    return {"matches": cursor.fetchall()}

# ═══════════════════════════════════════════════════════════════════════════════
# СЕРВЕРНЫЙ ФЛОТ
# ═══════════════════════════════════════════════════════════════════════════════

@app.post("/server/register")
def server_register(server_id: str, version: str = "1.0.0", db=Depends(get_db)):
    cursor = db.cursor()
    cursor.execute("""
        INSERT INTO game_servers (id, status, version, last_ping)
        VALUES (%s, 'idle', %s, NOW())
        ON DUPLICATE KEY UPDATE status='idle', version=%s, last_ping=NOW()
    """, (server_id, version, version))
    db.commit()

    # FIX v5.1: когда новый сервер выходит online — пробуем сматчить игроков из очереди
    cursor2 = db.cursor(dictionary=True)
    cursor2.execute("""
        SELECT mq.player_id, mq.player_name, mq.rating
        FROM matchmaking_queue mq
        ORDER BY mq.joined_at ASC
        LIMIT 1
    """)
    first_in_queue = cursor2.fetchone()

    if first_in_queue:
        player_obj = {
            "id": first_in_queue["player_id"],
            "username": first_in_queue["player_name"],
            "rating": first_in_queue["rating"]
        }
        match = _try_create_match(player_obj, db)
        if match:
            logger.info(
                f"Server {server_id} came online → match created: "
                f"session={match['session_id']}"
            )

    return {"success": True}

@app.get("/server/poll")
def server_poll(server_id: str, db=Depends(get_db)):
    cursor = db.cursor(dictionary=True)
    cursor.execute("UPDATE game_servers SET last_ping=NOW() WHERE id=%s", (server_id,))
    db.commit()

    cursor.execute("""
        SELECT gs.id, gs.join_code, gs.player1_id, gs.player2_id,
               p1.username AS p1_name, p2.username AS p2_name
        FROM game_sessions gs
        LEFT JOIN players p1 ON gs.player1_id = p1.id
        LEFT JOIN players p2 ON gs.player2_id = p2.id
        WHERE gs.server_id=%s AND gs.status='waiting'
        LIMIT 1
    """, (server_id,))
    row = cursor.fetchone()

    if row:
        return {
            "has_match": True,
            "session_id": row["id"],
            "join_code": row.get("join_code"),
            "player1_name": row.get("p1_name", ""),
            "player2_name": row.get("p2_name", "")
        }
    return {"has_match": False}


@app.post("/server/match_ready")
def server_match_ready(
    server_id:  str,
    session_id: int = 0,
    join_code:  str = "",
    room_id:    int = 0,
    db=Depends(get_db)
):
    cursor = db.cursor()
    if room_id > 0:
        cursor.execute("""
            UPDATE rooms SET join_code=%s WHERE id=%s AND server_id=%s
        """, (join_code, room_id, server_id))
        logger.info(f"match_ready [room]: server={server_id}, room={room_id}, join_code={join_code}")
    elif session_id > 0:
        cursor.execute("""
            UPDATE game_sessions
            SET join_code=%s, status='active'
            WHERE id=%s AND server_id=%s
        """, (join_code, session_id, server_id))
        logger.info(f"match_ready [session]: server={server_id}, session={session_id}, join_code={join_code}")
    else:
        logger.warning(f"match_ready: нет session_id и room_id! server={server_id}")
        raise HTTPException(400, "session_id or room_id required")
    db.commit()
    return {"success": True}


@app.post("/server/match_ended")
def server_match_ended(server_id: str, session_id: int = 0, db=Depends(get_db)):
    cursor = db.cursor()

    if session_id > 0:
        cursor.execute(
            "UPDATE game_sessions SET status='finished' WHERE id=%s",
            (session_id,)
        )
        logger.info(f"match_ended: session {session_id} -> finished")

    cursor.execute("""
        UPDATE game_sessions
        SET status='finished'
        WHERE server_id=%s AND status IN ('waiting', 'active')
    """, (server_id,))
    closed_count = cursor.rowcount
    if closed_count > 0:
        logger.info(f"match_ended: закрыто {closed_count} висящих сессий на сервере {server_id}")

    cursor.execute(
        "UPDATE game_servers SET status='idle' WHERE id=%s",
        (server_id,)
    )
    db.commit()
    logger.info(f"match_ended: server={server_id} -> idle")
    return {"success": True, "closed_sessions": closed_count}


@app.post("/server/match_failed")
def server_match_failed(
    server_id:  str,
    session_id: int = 0,
    error:      str = "",
    db=Depends(get_db)
):
    cursor = db.cursor()

    if session_id > 0:
        cursor.execute(
            "UPDATE game_sessions SET status='abandoned' WHERE id=%s",
            (session_id,)
        )

    cursor.execute("""
        UPDATE game_sessions
        SET status='abandoned'
        WHERE server_id=%s AND status IN ('waiting', 'active')
    """, (server_id,))

    cursor.execute(
        "UPDATE game_servers SET status='idle' WHERE id=%s",
        (server_id,)
    )
    db.commit()
    logger.warning(f"match_failed: server={server_id}, session={session_id}, error={error}")
    return {"success": True}


@app.get("/server/status")
def server_status(db=Depends(get_db)):
    cursor = db.cursor(dictionary=True)
    cursor.execute("SELECT * FROM game_servers ORDER BY last_ping DESC")
    return {"servers": cursor.fetchall()}

# ═══════════════════════════════════════════════════════════════════════════════
# МАТЧМЕЙКИНГ — v5.1
# ═══════════════════════════════════════════════════════════════════════════════

def _try_create_match(player: dict, db) -> dict | None:
    cursor = db.cursor(dictionary=True)
    try:
        cursor.execute("START TRANSACTION")

        cursor.execute(
            "SELECT * FROM matchmaking_queue WHERE player_id=%s FOR UPDATE",
            (player["id"],)
        )
        if not cursor.fetchone():
            cursor.execute("ROLLBACK")
            return None

        rating_min = player["rating"] - 300
        rating_max = player["rating"] + 300
        cursor.execute("""
            SELECT * FROM matchmaking_queue
            WHERE player_id != %s AND rating BETWEEN %s AND %s
            ORDER BY joined_at ASC LIMIT 1
            FOR UPDATE
        """, (player["id"], rating_min, rating_max))
        opponent = cursor.fetchone()

        if not opponent:
            cursor.execute("ROLLBACK")
            return None

        server = _find_alive_idle_server(cursor)
        if not server:
            cursor.execute("ROLLBACK")
            return None

        cursor.execute("""
            INSERT INTO game_sessions (server_id, player1_id, player2_id, status, created_at)
            VALUES (%s, %s, %s, 'waiting', NOW())
        """, (server["id"], player["id"], opponent["player_id"]))
        session_id = cursor.lastrowid

        cursor.execute(
            "DELETE FROM matchmaking_queue WHERE player_id IN (%s, %s)",
            (player["id"], opponent["player_id"])
        )
        cursor.execute("COMMIT")

        logger.info(
            f"Match created: session={session_id}, "
            f"{player['username']}({player['id']}) vs "
            f"{opponent['player_name']}({opponent['player_id']}), "
            f"server={server['id']}"
        )
        return {"session_id": session_id, "opponent": opponent["player_name"]}

    except Exception as e:
        try:
            cursor.execute("ROLLBACK")
        except Exception:
            pass
        logger.error(f"_try_create_match error: {e}")
        return None


@app.post("/matchmaking/join")
def matchmaking_join(request: Request, db=Depends(get_db)):
    ip = _get_client_ip(request)
    if not _check_rate_limit(ip, _matchmaking_attempts, MAX_MATCHMAKING_JOINS):
        raise HTTPException(429, "Too many matchmaking requests")

    player = _require_auth(request, db)
    cursor = db.cursor(dictionary=True)

    cursor.execute("SELECT id FROM matchmaking_queue WHERE player_id=%s", (player["id"],))
    if cursor.fetchone():
        return {"status": "already_in_queue"}

    cursor.execute("""
        UPDATE game_sessions
        SET status = 'abandoned'
        WHERE (player1_id = %s OR player2_id = %s)
          AND status IN ('waiting', 'active')
    """, (player["id"], player["id"]))
    if cursor.rowcount > 0:
        logger.info(f"Cleaned {cursor.rowcount} stale session(s) for player {player['id']}")
    db.commit()

    match = _try_create_match(player, db)
    if match:
        return {
            "status": "match_found",
            "session_id": match["session_id"],
            "opponent": match["opponent"]
        }

    cursor = db.cursor()
    cursor.execute(
        "INSERT IGNORE INTO matchmaking_queue (player_id, player_name, rating) VALUES (%s, %s, %s)",
        (player["id"], player["username"], player["rating"])
    )
    db.commit()

    check_cursor = db.cursor(dictionary=True)
    if not _find_alive_idle_server(check_cursor):
        _spawn_new_server()
        logger.info(f"No idle servers, spawning new one for player {player['username']}")
        return {"status": "waiting_for_server"}

    return {"status": "in_queue"}


@app.get("/matchmaking/status")
def matchmaking_status(request: Request, db=Depends(get_db)):
    player = _require_auth(request, db)
    cursor = db.cursor(dictionary=True)

    cursor.execute("""
        SELECT gs.*, p1.username AS p1_name, p2.username AS p2_name
        FROM game_sessions gs
        LEFT JOIN players p1 ON gs.player1_id = p1.id
        LEFT JOIN players p2 ON gs.player2_id = p2.id
        WHERE (gs.player1_id=%s OR gs.player2_id=%s)
          AND gs.status IN ('waiting', 'active')
        ORDER BY gs.id DESC LIMIT 1
    """, (player["id"], player["id"]))
    session = cursor.fetchone()

    if session:
        if session["status"] == "active" and session.get("join_code"):
            opponent_name = session["p2_name"] if session["player1_id"] == player["id"] else session["p1_name"]
            return {
                "found": True,
                "session_id": session["id"],
                "join_code": session["join_code"],
                "opponent_name": opponent_name,
                "status": "active"
            }
        return {"found": False, "status": "waiting_for_relay", "session_id": session["id"]}

    cursor.execute("SELECT * FROM matchmaking_queue WHERE player_id=%s", (player["id"],))
    queue = cursor.fetchone()

    if queue:
        # FIX v5.1: пробуем создать матч при каждом поллинге статуса
        match = _try_create_match(player, db)
        if match:
            logger.info(
                f"Match created from status poll for player {player['username']}: "
                f"session={match['session_id']}"
            )
            return {"found": False, "status": "waiting_for_relay", "session_id": match["session_id"]}

        cursor2 = db.cursor(dictionary=True)
        cursor2.execute(
            "SELECT COUNT(*) AS cnt FROM matchmaking_queue WHERE joined_at <= %s",
            (queue["joined_at"],)
        )
        pos = cursor2.fetchone()["cnt"]
        return {"found": False, "status": "in_queue", "queue_position": pos}

    return {"found": False, "status": "not_in_queue"}


@app.delete("/matchmaking/leave")
def matchmaking_leave(request: Request, db=Depends(get_db)):
    player = _require_auth(request, db)
    cursor = db.cursor()
    cursor.execute("DELETE FROM matchmaking_queue WHERE player_id=%s", (player["id"],))
    cursor.execute("""
        UPDATE game_sessions
        SET status='abandoned'
        WHERE (player1_id=%s OR player2_id=%s) AND status='waiting'
    """, (player["id"], player["id"]))
    db.commit()
    return {"success": True}

@app.get("/matchmaking/queue")
def matchmaking_queue_size(db=Depends(get_db)):
    cursor = db.cursor(dictionary=True)
    cursor.execute("SELECT COUNT(*) AS cnt FROM matchmaking_queue")
    return {"size": cursor.fetchone()["cnt"]}


# ═══════════════════════════════════════════════════════════════════════════════
# КОМНАТЫ (Play with Friend) — v2
# ═══════════════════════════════════════════════════════════════════════════════
#
# Логика:
#   1. Хост вызывает POST /rooms/create → получает 6-символьный код
#   2. Хост создаёт Relay → вызывает POST /rooms/{code}/relay с join_code
#   3. Гость вызывает GET /room/find — поллит пока has_relay=true
#   4. Гость подключается к Relay → NGO синхронизирует сцену от хоста
#   5. Рейтинг не меняется (is_room_match=true в /matches/result)
#
# Экспайри (cleanup в _startup_cleanup):
#   - Пустая комната (нет гостя) → удаляется через 5 минут
#   - Любая открытая комната → закрывается через 20 минут

@app.post("/rooms/create")
def rooms_create(request: Request, db=Depends(get_db)):
    player = _require_auth(request, db)

    # Закрываем старые открытые комнаты этого игрока
    cursor = db.cursor()
    cursor.execute(
        "UPDATE rooms SET is_open = 0 WHERE host_id = %s AND is_open = 1",
        (player["id"],)
    )

    code = "".join(random.choices(string.ascii_uppercase + string.digits, k=6))
    # is_open=1 явно — не полагаемся на DEFAULT (может быть NULL при некоторых миграциях)
    cursor.execute(
        "INSERT INTO rooms (code, host_id, is_open, created_at) VALUES (%s, %s, 1, NOW())",
        (code, player["id"])
    )
    db.commit()
    logger.info(f"Room created: {code} by {player['username']}")
    return {"success": True, "code": code, "room_id": cursor.lastrowid}


@app.post("/rooms/{code}/relay")
def room_set_relay(code: str, join_code: str, request: Request, db=Depends(get_db)):
    """
    Хост вызывает после создания Relay-аллокации.
    Сохраняет join_code в комнате — гость получит его при следующем GET /room/find.
    """
    player = _require_auth(request, db)
    cursor = db.cursor(dictionary=True)

    cursor.execute("SELECT * FROM rooms WHERE code=%s AND is_open=1", (code,))
    room = cursor.fetchone()
    if not room:
        raise HTTPException(404, "Room not found or already closed")
    if room["host_id"] != player["id"]:
        raise HTTPException(403, "Only the host can set relay code")

    cursor2 = db.cursor()
    cursor2.execute("UPDATE rooms SET join_code=%s WHERE code=%s", (join_code, code))
    db.commit()
    logger.info(f"Room {code}: relay set by {player['username']}, join_code={join_code}")
    return {"success": True}


@app.get("/room/find")
def room_find(code: str, request: Request, db=Depends(get_db)):
    player = _require_auth(request, db)
    cursor = db.cursor(dictionary=True)

    # Ищем открытую комнату не старше 20 минут
    cursor.execute("""
        SELECT r.*, p.username AS host_name
        FROM rooms r
        JOIN players p ON r.host_id = p.id
        WHERE r.code=%s AND r.is_open=1
          AND (r.created_at IS NULL OR TIMESTAMPDIFF(MINUTE, r.created_at, NOW()) <= 20)
    """, (code,))
    room = cursor.fetchone()
    if not room:
        raise HTTPException(404, "Room not found, closed, or expired")

    # Регистрируем гостя (если ещё не зарегистрирован и это не хост)
    if room.get("guest_id") is None and room["host_id"] != player["id"]:
        cursor2 = db.cursor()
        cursor2.execute(
            "UPDATE rooms SET guest_id=%s WHERE code=%s AND guest_id IS NULL",
            (player["id"], code)
        )
        db.commit()

    has_relay = bool(room.get("join_code"))
    return {
        "found": True,
        "code": room["code"],
        "join_code": room.get("join_code") or "",
        "host_name": room["host_name"],
        "has_relay": has_relay   # false пока хост не создал Relay
    }


@app.delete("/rooms/{code}")
def room_close(code: str, request: Request, db=Depends(get_db)):
    """Закрывает комнату. Вызывается хостом при выходе или старте игры."""
    player = _require_auth(request, db)
    cursor = db.cursor()
    cursor.execute(
        "UPDATE rooms SET is_open=0 WHERE code=%s AND host_id=%s",
        (code, player["id"])
    )
    db.commit()
    return {"success": True}

# ═══════════════════════════════════════════════════════════════════════════════
# РЕЗУЛЬТАТЫ МАТЧЕЙ
# ═══════════════════════════════════════════════════════════════════════════════

def _calculate_elo(winner_rating: int, loser_rating: int, is_technical: bool = False):
    K = 32
    expected = 1 / (1 + 10 ** ((loser_rating - winner_rating) / 400))
    winner_change = round(K * (1 - expected))
    loser_change = round(K * (0 - (1 - expected)))

    if is_technical:
        winner_change = max(1, round(winner_change * 0.5))
        loser_change = min(-1, round(loser_change * 0.5))

    return winner_change, loser_change


@app.post("/matches/result")
def matches_result(data: MatchResultRequest, request: Request, db=Depends(get_db)):
    player = _require_auth(request, db)
    cursor = db.cursor(dictionary=True)

    cursor.execute("SELECT * FROM players WHERE username=%s", (data.winner_name,))
    winner = cursor.fetchone()
    cursor.execute("SELECT * FROM players WHERE username=%s", (data.loser_name,))
    loser = cursor.fetchone()

    if not winner or not loser:
        raise HTTPException(404, "Player not found")

    # [rooms_v2] Комнатный матч — ELO не меняется, только статистика убийств
    if data.is_room_match:
        cursor2 = db.cursor()
        cursor2.execute("""
            UPDATE players SET
                total_kills=total_kills+%s, best_kills=GREATEST(best_kills,%s)
            WHERE id=%s
        """, (data.winner_kills, data.winner_kills, winner["id"]))
        cursor2.execute("""
            UPDATE players SET
                total_kills=total_kills+%s, best_kills=GREATEST(best_kills,%s)
            WHERE id=%s
        """, (data.loser_kills, data.loser_kills, loser["id"]))
        cursor2.execute("""
            INSERT INTO matches
              (player1_id,player2_id,winner_id,kills_p1,kills_p2,
               level_p1,level_p2,duration_sec,rating_change,is_technical,is_room_match)
            VALUES (%s,%s,%s,%s,%s,%s,%s,%s,0,%s,1)
        """, (winner["id"], loser["id"], winner["id"],
              data.winner_kills, data.loser_kills,
              data.winner_level, data.loser_level,
              data.duration_sec, data.is_technical))
        db.commit()
        logger.info(f"Room match saved (no ELO): {data.winner_name} beat {data.loser_name}")
        return {
            "success": True,
            "is_room_match": True,
            "winner_new_rating": winner["rating"],
            "loser_new_rating": loser["rating"],
            "winner_rating_change": 0,
            "loser_rating_change": 0,
        }

    # Обычный ranked матч — считаем ELO
    winner_change, loser_change = _calculate_elo(
        winner["rating"], loser["rating"], data.is_technical
    )
    new_winner_rating = winner["rating"] + winner_change
    new_loser_rating  = loser["rating"]  + loser_change

    cursor2 = db.cursor()
    cursor2.execute("""
        UPDATE players SET rating=%s, wins=wins+1,
            total_kills=total_kills+%s, best_kills=GREATEST(best_kills,%s)
        WHERE id=%s
    """, (new_winner_rating, data.winner_kills, data.winner_kills, winner["id"]))

    cursor2.execute("""
        UPDATE players SET rating=%s, losses=losses+1,
            total_kills=total_kills+%s, best_kills=GREATEST(best_kills,%s)
        WHERE id=%s
    """, (new_loser_rating, data.loser_kills, data.loser_kills, loser["id"]))

    cursor2.execute("""
        INSERT INTO matches
          (player1_id,player2_id,winner_id,kills_p1,kills_p2,
           level_p1,level_p2,duration_sec,rating_change,is_technical,is_room_match)
        VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,0)
    """, (winner["id"], loser["id"], winner["id"],
          data.winner_kills, data.loser_kills,
          data.winner_level, data.loser_level,
          data.duration_sec, winner_change, data.is_technical))
    db.commit()

    return {
        "success":              True,
        "winner_new_rating":    new_winner_rating,
        "loser_new_rating":     new_loser_rating,
        "winner_rating_change": winner_change,
        "loser_rating_change":  loser_change,
    }


@app.post("/server/save_match_result")
def server_save_match_result(
    data: MatchResultRequest,
    server_id: str = "",
    db=Depends(get_db)
):
    """Сохранение результата матча от Dedicated Server (без Bearer токена)."""
    cursor = db.cursor(dictionary=True)

    if server_id:
        cursor.execute("""
            SELECT id, status FROM game_servers WHERE id=%s
              AND TIMESTAMPDIFF(SECOND, last_ping, NOW()) < 300
        """, (server_id,))
        server = cursor.fetchone()
        if not server:
            raise HTTPException(403, "Unknown or expired server_id")
    else:
        raise HTTPException(403, "server_id required")

    cursor.execute("SELECT * FROM players WHERE username=%s", (data.winner_name,))
    winner = cursor.fetchone()
    cursor.execute("SELECT * FROM players WHERE username=%s", (data.loser_name,))
    loser = cursor.fetchone()

    if not winner or not loser:
        raise HTTPException(404, "Player not found")

    # [rooms_v2] Dedicated server тоже может проводить комнатные матчи без ELO
    if data.is_room_match:
        cursor2 = db.cursor()
        cursor2.execute("""
            UPDATE players SET
                total_kills=total_kills+%s, best_kills=GREATEST(best_kills,%s)
            WHERE id=%s
        """, (data.winner_kills, data.winner_kills, winner["id"]))
        cursor2.execute("""
            UPDATE players SET
                total_kills=total_kills+%s, best_kills=GREATEST(best_kills,%s)
            WHERE id=%s
        """, (data.loser_kills, data.loser_kills, loser["id"]))
        cursor2.execute("""
            INSERT INTO matches
              (player1_id,player2_id,winner_id,kills_p1,kills_p2,
               level_p1,level_p2,duration_sec,rating_change,is_technical,is_room_match)
            VALUES (%s,%s,%s,%s,%s,%s,%s,%s,0,%s,1)
        """, (winner["id"], loser["id"], winner["id"],
              data.winner_kills, data.loser_kills,
              data.winner_level, data.loser_level,
              data.duration_sec, data.is_technical))
        db.commit()
        logger.info(f"[Server] Room match saved (no ELO): {data.winner_name} beat {data.loser_name}")
        return {
            "success": True,
            "is_room_match": True,
            "winner_new_rating": winner["rating"],
            "loser_new_rating": loser["rating"],
            "winner_rating_change": 0,
            "loser_rating_change": 0,
        }

    winner_change, loser_change = _calculate_elo(
        winner["rating"], loser["rating"], data.is_technical
    )
    new_winner_rating = winner["rating"] + winner_change
    new_loser_rating  = loser["rating"]  + loser_change

    cursor2 = db.cursor()
    cursor2.execute("""
        UPDATE players SET rating=%s, wins=wins+1,
            total_kills=total_kills+%s, best_kills=GREATEST(best_kills,%s)
        WHERE id=%s
    """, (new_winner_rating, data.winner_kills, data.winner_kills, winner["id"]))

    cursor2.execute("""
        UPDATE players SET rating=%s, losses=losses+1,
            total_kills=total_kills+%s, best_kills=GREATEST(best_kills,%s)
        WHERE id=%s
    """, (new_loser_rating, data.loser_kills, data.loser_kills, loser["id"]))

    cursor2.execute("""
        INSERT INTO matches
          (player1_id,player2_id,winner_id,kills_p1,kills_p2,
           level_p1,level_p2,duration_sec,rating_change,is_technical,is_room_match)
        VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,0)
    """, (winner["id"], loser["id"], winner["id"],
          data.winner_kills, data.loser_kills,
          data.winner_level, data.loser_level,
          data.duration_sec, winner_change, data.is_technical))
    db.commit()

    return {
        "success":              True,
        "winner_new_rating":    new_winner_rating,
        "loser_new_rating":     new_loser_rating,
        "winner_rating_change": winner_change,
        "loser_rating_change":  loser_change,
    }

# ═══════════════════════════════════════════════════════════════════════════════
# ЛИДЕРБОРД
# ═══════════════════════════════════════════════════════════════════════════════

@app.get("/leaderboard")
def get_leaderboard(limit: int = 20, db=Depends(get_db)):
    cursor = db.cursor(dictionary=True)
    cursor.execute("""
        SELECT ROW_NUMBER() OVER (ORDER BY rating DESC) AS rank,
               username, rating, wins, losses, total_kills, best_kills
        FROM players ORDER BY rating DESC LIMIT %s
    """, (min(limit, 100),))
    return {"entries": cursor.fetchall()}


# ═══════════════════════════════════════════════════════════════════════════════
# ADMIN
# ═══════════════════════════════════════════════════════════════════════════════

def _require_server_secret(request: Request):
    secret = request.headers.get("X-Server-Secret", "")
    if not secret or secret != SERVER_API_SECRET:
        raise HTTPException(403, "Invalid or missing X-Server-Secret header")

@app.post("/admin/cleanup")
def admin_cleanup(request: Request, db=Depends(get_db)):
    _require_server_secret(request)
    cursor = db.cursor()

    cursor.execute("""
        UPDATE game_sessions
        SET status = 'abandoned'
        WHERE status = 'waiting'
          AND TIMESTAMPDIFF(SECOND, created_at, NOW()) > 120
    """)
    stale_sessions = cursor.rowcount

    cursor.execute("""
        DELETE mq FROM matchmaking_queue mq
        LEFT JOIN game_sessions gs
          ON (gs.player1_id = mq.player_id OR gs.player2_id = mq.player_id)
          AND gs.status IN ('waiting', 'active')
        WHERE gs.id IS NULL
    """)
    cleared_queue = cursor.rowcount

    cursor.execute("""
        DELETE FROM game_servers
        WHERE TIMESTAMPDIFF(SECOND, last_ping, NOW()) > 300
    """)
    dead_servers = cursor.rowcount

    # [rooms_v2] Очищаем протухшие комнаты (безопасно если migration не применена)
    expired_rooms = 0
    try:
        cursor.execute("""
            DELETE FROM rooms
            WHERE is_open = 1
              AND guest_id IS NULL
              AND created_at IS NOT NULL
              AND TIMESTAMPDIFF(MINUTE, created_at, NOW()) > 5
        """)
        expired_rooms = cursor.rowcount
        cursor.execute("""
            UPDATE rooms SET is_open = 0
            WHERE is_open = 1 AND created_at IS NOT NULL
              AND TIMESTAMPDIFF(MINUTE, created_at, NOW()) > 20
        """)
        expired_rooms += cursor.rowcount
    except Exception as e:
        logger.debug(f"admin_cleanup [rooms] skipped: {e}")

    db.commit()
    logger.info(
        f"admin_cleanup: sessions={stale_sessions}, queue={cleared_queue}, "
        f"servers={dead_servers}, expired_rooms={expired_rooms}"
    )
    return {
        "success": True,
        "stale_sessions_abandoned": stale_sessions,
        "queue_entries_cleared": cleared_queue,
        "dead_servers_removed": dead_servers,
        "expired_rooms_removed": expired_rooms,
    }


# ═══════════════════════════════════════════════════════════════════════════════
# LIVE MATCH TRACKING
# ═══════════════════════════════════════════════════════════════════════════════

@app.post("/live/match_update")
def live_match_update(data: LiveMatchUpdateRequest, db=Depends(get_db)):
    cursor = db.cursor()

    for p in data.players:
        cursor.execute("""
            INSERT INTO live_match_players
              (session_id, client_id, username, character_index, level, kills,
               health, max_health, shield, match_time_seconds)
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            ON DUPLICATE KEY UPDATE
              username=%s, character_index=%s, level=%s, kills=%s,
              health=%s, max_health=%s, shield=%s, match_time_seconds=%s
        """, (
            data.session_id, p.client_id, p.username, p.character_index, p.level, p.kills,
            p.health, p.max_health, p.shield, data.match_time_seconds,
            p.username, p.character_index, p.level, p.kills,
            p.health, p.max_health, p.shield, data.match_time_seconds
        ))

    for p in data.players:
        for w in p.weapons:
            cursor.execute("""
                INSERT INTO live_match_weapons
                  (session_id, client_id, weapon_slot_index, weapon_id, weapon_name, weapon_level)
                VALUES (%s, %s, %s, %s, %s, %s)
                ON DUPLICATE KEY UPDATE
                  weapon_id=%s, weapon_name=%s, weapon_level=%s,
                  updated_at=NOW()
            """, (data.session_id, p.client_id, w.slot_index, w.weapon_id, w.weapon_name, w.weapon_level,
                  w.weapon_id, w.weapon_name, w.weapon_level))

    for p in data.players:
        for t in p.totems:
            cursor.execute("""
                INSERT INTO live_match_totems
                  (session_id, client_id, totem_slot_index, totem_id, totem_name,
                   bonus_type, totem_level, totem_value)
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s)
                ON DUPLICATE KEY UPDATE
                  totem_id=%s, totem_name=%s, bonus_type=%s, totem_level=%s, totem_value=%s,
                  updated_at=NOW()
            """, (data.session_id, p.client_id, t.slot_index, t.totem_id, t.totem_name,
                  t.bonus_type, t.totem_level, t.totem_value,
                  t.totem_id, t.totem_name, t.bonus_type, t.totem_level, t.totem_value))

    for p in data.players:
        for item in p.items:
            cursor.execute("""
                INSERT INTO live_match_items
                  (session_id, client_id, item_id, item_name, item_rarity, quantity)
                VALUES (%s, %s, %s, %s, %s, %s)
                ON DUPLICATE KEY UPDATE
                  item_name=%s, item_rarity=%s, quantity=%s,
                  updated_at=NOW()
            """, (data.session_id, p.client_id, item.item_id, item.item_name, item.item_rarity, item.quantity,
                  item.item_name, item.item_rarity, item.quantity))

    db.commit()
    return {
        "success": True,
        "session_id": data.session_id,
        "players_updated": len(data.players),
        "match_time_seconds": data.match_time_seconds
    }


@app.get("/live/match/{session_id}")
def get_live_match(session_id: int, db=Depends(get_db)):
    cursor = db.cursor(dictionary=True)

    cursor.execute("""
        SELECT lmp.*, gs.status AS match_status
        FROM live_match_players lmp
        LEFT JOIN game_sessions gs ON lmp.session_id = gs.id
        WHERE lmp.session_id = %s
        ORDER BY lmp.client_id
    """, (session_id,))
    players = cursor.fetchall()

    cursor.execute("""
        SELECT * FROM live_match_weapons WHERE session_id = %s ORDER BY client_id, weapon_slot_index
    """, (session_id,))
    weapons = cursor.fetchall()

    cursor.execute("""
        SELECT * FROM live_match_totems WHERE session_id = %s ORDER BY client_id, totem_slot_index
    """, (session_id,))
    totems = cursor.fetchall()

    return {"session_id": session_id, "players": players, "weapons": weapons, "totems": totems}


@app.get("/live/match/{session_id}/player/{client_id}")
def get_live_player(session_id: int, client_id: int, db=Depends(get_db)):
    cursor = db.cursor(dictionary=True)

    cursor.execute("""
        SELECT * FROM live_match_players WHERE session_id=%s AND client_id=%s
    """, (session_id, client_id))
    player = cursor.fetchone()

    cursor.execute("""
        SELECT * FROM live_match_weapons WHERE session_id=%s AND client_id=%s ORDER BY weapon_slot_index
    """, (session_id, client_id))
    weapons = cursor.fetchall()

    cursor.execute("""
        SELECT * FROM live_match_totems WHERE session_id=%s AND client_id=%s ORDER BY totem_slot_index
    """, (session_id, client_id))
    totems = cursor.fetchall()

    return {"player": player, "weapons": weapons, "totems": totems}


@app.delete("/live/match/{session_id}/cleanup")
def live_match_cleanup(session_id: int, db=Depends(get_db)):
    cursor = db.cursor()
    cursor.execute("DELETE FROM live_match_players WHERE session_id=%s", (session_id,))
    cursor.execute("DELETE FROM live_match_weapons WHERE session_id=%s", (session_id,))
    cursor.execute("DELETE FROM live_match_totems WHERE session_id=%s", (session_id,))
    db.commit()
    return {"success": True, "deleted_rows": cursor.rowcount}


# ═══════════════════════════════════════════════════════════════════════════════
# ВСПОМОГАТЕЛЬНЫЕ ФУНКЦИИ
# ═══════════════════════════════════════════════════════════════════════════════

def _find_alive_idle_server(cursor):
    cursor.execute("""
        SELECT * FROM game_servers
        WHERE status='idle'
          AND TIMESTAMPDIFF(SECOND, last_ping, NOW()) < 120
        ORDER BY last_ping ASC LIMIT 1
    """)
    return cursor.fetchone()

def _spawn_new_server():
    global _last_server_spawn
    now = time.time()
    if now - _last_server_spawn < 3:
        return False
    _last_server_spawn = now
    try:
        os.makedirs(_SERVER_LOG_DIR, exist_ok=True)
        ts = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        log_file = os.path.join(_SERVER_LOG_DIR, f"server_{ts}.log")
        proc = subprocess.Popen(
            [_GAME_EXE_PATH, "-batchmode", "-nographics", "-fastApiUrl", "http://localhost:8000"],
            stdout=open(log_file, "w"),
            stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NEW_CONSOLE if os.name == "nt" else 0
        )
        _spawned_server_pids[proc.pid] = {"started": datetime.datetime.now().isoformat(), "log": log_file}
        return True
    except Exception as e:
        logger.error(f"Failed to spawn server: {e}")
        return False
