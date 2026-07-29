CREATE DATABASE IF NOT EXISTS rivalveil
    CHARACTER SET utf8mb4
    COLLATE utf8mb4_unicode_ci;

USE rivalveil;

SET NAMES utf8mb4;

-- ═══════════════════════════════════════════════════════════════════════════════
-- ТАБЛИЦЫ
-- ═══════════════════════════════════════════════════════════════════════════════

-- ─── players ───────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS players (
    id INT AUTO_INCREMENT PRIMARY KEY,
    username VARCHAR(32) NOT NULL UNIQUE,
    password_hash VARCHAR(255) NOT NULL,
    rating INT DEFAULT 1000,
    wins INT DEFAULT 0,
    losses INT DEFAULT 0,
    total_kills INT DEFAULT 0,
    best_kills INT DEFAULT 0,
    created_at DATETIME DEFAULT NOW()
);

-- ─── sessions ──────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS sessions (
    id INT AUTO_INCREMENT PRIMARY KEY,
    player_id INT NOT NULL UNIQUE,
    token VARCHAR(64) NOT NULL UNIQUE,
    expires_at DATETIME NOT NULL,
    FOREIGN KEY (player_id) REFERENCES players(id) ON DELETE CASCADE
);

-- ─── matches ───────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS matches (
    id INT AUTO_INCREMENT PRIMARY KEY,
    player1_id INT NOT NULL,
    player2_id INT NOT NULL,
    winner_id INT,
    kills_p1 INT DEFAULT 0,
    kills_p2 INT DEFAULT 0,
    level_p1 INT DEFAULT 1,
    level_p2 INT DEFAULT 1,
    duration_sec INT DEFAULT 0,
    rating_change INT DEFAULT 0,
    is_technical TINYINT(1) DEFAULT 0,
    played_at DATETIME DEFAULT NOW(),
    FOREIGN KEY (player1_id) REFERENCES players(id) ON DELETE CASCADE,
    FOREIGN KEY (player2_id) REFERENCES players(id) ON DELETE CASCADE,
    FOREIGN KEY (winner_id) REFERENCES players(id) ON DELETE SET NULL
);

-- ─── matchmaking_queue ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS matchmaking_queue (
    id INT AUTO_INCREMENT PRIMARY KEY,
    player_id INT NOT NULL UNIQUE,
    player_name VARCHAR(32),
    rating INT DEFAULT 1000,
    joined_at DATETIME DEFAULT NOW(),
    FOREIGN KEY (player_id) REFERENCES players(id) ON DELETE CASCADE
);

-- ─── game_servers (флот Dedicated Server) ────────────────────────────────────
CREATE TABLE IF NOT EXISTS game_servers (
    id VARCHAR(36) PRIMARY KEY,
    status ENUM('idle','busy') DEFAULT 'idle',
    version VARCHAR(20) DEFAULT '1.0.0',
    last_ping DATETIME DEFAULT NOW()
);

-- ─── game_sessions (текущие матчи) ─────────────────────────────────────────────
-- ИСПРАВЛЕНО v5.0: добавлен created_at для корректной очистки висящих сессий
CREATE TABLE IF NOT EXISTS game_sessions (
    id INT AUTO_INCREMENT PRIMARY KEY,
    server_id VARCHAR(36),
    join_code VARCHAR(20),
    player1_id INT,
    player2_id INT,
    status ENUM('waiting','active','finished','abandoned') DEFAULT 'waiting',
    created_at DATETIME DEFAULT NOW(),
    FOREIGN KEY (server_id) REFERENCES game_servers(id) ON DELETE SET NULL,
    FOREIGN KEY (player1_id) REFERENCES players(id) ON DELETE SET NULL,
    FOREIGN KEY (player2_id) REFERENCES players(id) ON DELETE SET NULL
);

-- ─── rooms (Play with Friend) ──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS rooms (
    id INT AUTO_INCREMENT PRIMARY KEY,
    code VARCHAR(8) NOT NULL UNIQUE,
    host_id INT NOT NULL,
    server_id VARCHAR(36),
    join_code VARCHAR(20),
    is_open TINYINT(1) DEFAULT 1,
    FOREIGN KEY (host_id) REFERENCES players(id) ON DELETE CASCADE,
    FOREIGN KEY (server_id) REFERENCES game_servers(id) ON DELETE SET NULL
);

-- ═══════════════════════════════════════════════════════════════════════════════
-- ИНДЕКСЫ
-- ═══════════════════════════════════════════════════════════════════════════════

-- matchmaking_queue: индекс для BETWEEN rating AND ... ORDER BY joined_at
-- ИСПРАВЛЕНО: idx_mm_rating покрывает range scan по rating + sort по joined_at
CREATE INDEX idx_mm_rating       ON matchmaking_queue(rating, joined_at);
CREATE INDEX idx_sess_p1_status  ON game_sessions(player1_id, status);
CREATE INDEX idx_sess_p2_status  ON game_sessions(player2_id, status);
CREATE INDEX idx_sess_server     ON game_sessions(server_id, status);
CREATE INDEX idx_sess_created    ON game_sessions(created_at);
CREATE INDEX idx_srv_status      ON game_servers(status, last_ping);
CREATE INDEX idx_matches_p1      ON matches(player1_id, played_at);
CREATE INDEX idx_matches_p2      ON matches(player2_id, played_at);

-- ═══════════════════════════════════════════════════════════════════════════════
-- ТЕСТОВЫЕ ДАННЫЕ
-- ═══════════════════════════════════════════════════════════════════════════════

INSERT INTO players (username, password_hash, rating, wins, losses, total_kills, best_kills) VALUES
('ProGamer', '$2b$12$EixZaYVK1fsbw1ZfbX3OXePaWxn96p36WQoeG6Lruj3vjPGga31lW', 1450, 23, 5,  890, 67),
('CoolDude', '$2b$12$EixZaYVK1fsbw1ZfbX3OXePaWxn96p36WQoeG6Lruj3vjPGga31lW', 1280, 15, 8,  620, 54),
('TestUser', '$2b$12$EixZaYVK1fsbw1ZfbX3OXePaWxn96p36WQoeG6Lruj3vjPGga31lW', 1000,  5, 3,  210, 38);

-- ═══════════════════════════════════════════════════════════════════════════════
-- ДИАГНОСТИКА (закомментированные примеры SQL)
-- ═══════════════════════════════════════════════════════════════════════════════

-- Лидерборд:
-- SELECT ROW_NUMBER() OVER (ORDER BY rating DESC) AS rank,
--        username, rating, wins, losses, total_kills, best_kills
-- FROM players ORDER BY rating DESC;

-- Активные матчи:
-- SELECT gs.id, gs.status, gs.join_code, p1.username, p2.username, srv.id AS server
-- FROM game_sessions gs
-- JOIN players p1 ON gs.player1_id = p1.id
-- JOIN players p2 ON gs.player2_id = p2.id
-- JOIN game_servers srv ON gs.server_id = srv.id
-- WHERE gs.status IN ('waiting','active');

-- Флот серверов:
-- SELECT id, status, version, TIMESTAMPDIFF(SECOND, last_ping, NOW()) AS secs_ago
-- FROM game_servers ORDER BY status, last_ping;

-- ═══════════════════════════════════════════════════════════════════════════════
-- ─── LIVE МАТЧИ (Real-time tracking) ───────────────────────────────────────────
-- Таблицы для записи состояния матча в прямом эфире (каждые 5 секунд с Dedicated Server)
-- ═══════════════════════════════════════════════════════════════════════════════

CREATE TABLE IF NOT EXISTS live_match_players (
    id INT PRIMARY KEY AUTO_INCREMENT,
    session_id INT NOT NULL,
    client_id BIGINT NOT NULL,
    username VARCHAR(32) DEFAULT 'Unknown',
    character_index INT DEFAULT -1,
    level INT DEFAULT 1,
    kills INT DEFAULT 0,
    health FLOAT DEFAULT 100.0,
    max_health FLOAT DEFAULT 100.0,
    shield FLOAT DEFAULT 0.0,
    match_time_seconds INT DEFAULT 0,
    updated_at DATETIME DEFAULT NOW() ON UPDATE NOW(),
    FOREIGN KEY (session_id) REFERENCES game_sessions(id) ON DELETE CASCADE,
    UNIQUE KEY uk_session_client (session_id, client_id),
    INDEX idx_session_updated (session_id, updated_at)
);

-- ИСПРАВЛЕНО v5.0: добавлен UNIQUE KEY для ON DUPLICATE KEY UPDATE
CREATE TABLE IF NOT EXISTS live_match_weapons (
    id INT PRIMARY KEY AUTO_INCREMENT,
    session_id INT NOT NULL,
    client_id BIGINT NOT NULL,
    weapon_slot_index TINYINT DEFAULT 0,
    weapon_id VARCHAR(32) NOT NULL,
    weapon_name VARCHAR(64),
    weapon_level INT DEFAULT 1,
    updated_at DATETIME DEFAULT NOW() ON UPDATE NOW(),
    FOREIGN KEY (session_id) REFERENCES game_sessions(id) ON DELETE CASCADE,
    UNIQUE KEY uk_session_client_slot (session_id, client_id, weapon_slot_index),
    INDEX idx_session_client (session_id, client_id)
);

-- ИСПРАВЛЕНО v5.0: добавлен UNIQUE KEY для ON DUPLICATE KEY UPDATE
CREATE TABLE IF NOT EXISTS live_match_totems (
    id INT PRIMARY KEY AUTO_INCREMENT,
    session_id INT NOT NULL,
    client_id BIGINT NOT NULL,
    totem_slot_index TINYINT DEFAULT 0,
    totem_id VARCHAR(32) NOT NULL,
    totem_name VARCHAR(64),
    bonus_type VARCHAR(32),
    totem_level INT DEFAULT 1,
    totem_value FLOAT DEFAULT 0.0,
    updated_at DATETIME DEFAULT NOW() ON UPDATE NOW(),
    FOREIGN KEY (session_id) REFERENCES game_sessions(id) ON DELETE CASCADE,
    UNIQUE KEY uk_session_client_slot (session_id, client_id, totem_slot_index),
    INDEX idx_session_client (session_id, client_id)
);

-- Предметы требуют клиентской синхронизации (ItemInventory не NetworkBehaviour)
-- Раскомментируйте когда добавите синхронизацию инвентаря по сети
-- CREATE TABLE IF NOT EXISTS live_match_items (
--     id INT PRIMARY KEY AUTO_INCREMENT,
--     session_id INT NOT NULL,
--     client_id BIGINT NOT NULL,
--     item_id VARCHAR(32),
--     item_name VARCHAR(64),
--     item_rarity VARCHAR(16),
--     quantity INT DEFAULT 1,
--     updated_at DATETIME DEFAULT NOW() ON UPDATE NOW(),
--     FOREIGN KEY (session_id) REFERENCES game_sessions(id) ON DELETE CASCADE,
--     INDEX idx_session_client (session_id, client_id)
-- );
