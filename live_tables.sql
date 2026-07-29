USE rivalveil;

-- ═══════════════════════════════════════════════════════════════════════════════
-- LIVE TABLES v5.0 — для применения на существующей БД (миграция)
-- ═══════════════════════════════════════════════════════════════════════════════

-- ─── live_match_players ──────────────────────────────────────────────────────
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

-- ─── live_match_weapons ──────────────────────────────────────────────────────
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

-- ─── live_match_totems ───────────────────────────────────────────────────────
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

-- ─── live_match_items ─────────────────────────────────────────────────────────
-- Предметы из сундуков. Синхронизируются через PlayerStats.netItemFlags (NetworkVariable).
-- Архитектура: ItemInventory → ComputeItemFlags() → SyncItemFlags() → netItemFlags → LiveMatchTracker → БД
-- UNIQUE KEY нужен для ON DUPLICATE KEY UPDATE (upsert, как у weapons/totems)
CREATE TABLE IF NOT EXISTS live_match_items (
    id INT PRIMARY KEY AUTO_INCREMENT,
    session_id INT NOT NULL,
    client_id BIGINT NOT NULL,
    item_id VARCHAR(32) NOT NULL,
    item_name VARCHAR(64),
    item_rarity VARCHAR(16),
    quantity INT DEFAULT 1,
    updated_at DATETIME DEFAULT NOW() ON UPDATE NOW(),
    FOREIGN KEY (session_id) REFERENCES game_sessions(id) ON DELETE CASCADE,
    UNIQUE KEY uk_session_client_item (session_id, client_id, item_id),
    INDEX idx_session_client (session_id, client_id)
);
