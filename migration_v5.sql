-- ═══════════════════════════════════════════════════════════════════════════════
-- MIGRATION v4.3 → v5.0 (ИСПРАВЛЕНО: убран CREATE INDEX IF NOT EXISTS)
-- CREATE INDEX IF NOT EXISTS не поддерживается в MySQL < 8.0.1
-- Используем INFORMATION_SCHEMA.STATISTICS для проверки — как для UNIQUE KEY
-- ═══════════════════════════════════════════════════════════════════════════════

USE rivalveil;

-- ─────────────────────────────────────────────────────────────────────────────
-- 1. Добавляем created_at в game_sessions (если ещё нет)
-- ─────────────────────────────────────────────────────────────────────────────
SET @exists = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
               WHERE table_schema = DATABASE()
               AND table_name = 'game_sessions'
               AND column_name = 'created_at');

SET @sql = IF(@exists = 0,
    'ALTER TABLE game_sessions ADD COLUMN created_at DATETIME DEFAULT NOW()',
    'SELECT "created_at already exists"');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- ─────────────────────────────────────────────────────────────────────────────
-- 2. Индекс idx_sess_created на game_sessions(created_at)
--    ИСПРАВЛЕНО: CREATE INDEX IF NOT EXISTS → INFORMATION_SCHEMA check
-- ─────────────────────────────────────────────────────────────────────────────
SET @iexists = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
                WHERE table_schema = DATABASE()
                AND table_name = 'game_sessions'
                AND index_name = 'idx_sess_created');

SET @isql = IF(@iexists = 0,
    'ALTER TABLE game_sessions ADD INDEX idx_sess_created (created_at)',
    'SELECT "idx_sess_created already exists"');
PREPARE istmt FROM @isql;
EXECUTE istmt;
DEALLOCATE PREPARE istmt;

-- ─────────────────────────────────────────────────────────────────────────────
-- 3. UNIQUE KEY для live_match_weapons (если ещё нет)
-- ─────────────────────────────────────────────────────────────────────────────
SET @wexists = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
                WHERE table_schema = DATABASE()
                AND table_name = 'live_match_weapons'
                AND index_name = 'uk_session_client_slot');

SET @wsql = IF(@wexists = 0,
    'ALTER TABLE live_match_weapons ADD UNIQUE KEY uk_session_client_slot (session_id, client_id, weapon_slot_index)',
    'SELECT "uk_session_client_slot already exists on weapons"');
PREPARE wstmt FROM @wsql;
EXECUTE wstmt;
DEALLOCATE PREPARE wstmt;

-- ─────────────────────────────────────────────────────────────────────────────
-- 4. UNIQUE KEY для live_match_totems (если ещё нет)
-- ─────────────────────────────────────────────────────────────────────────────
SET @texists = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
                WHERE table_schema = DATABASE()
                AND table_name = 'live_match_totems'
                AND index_name = 'uk_session_client_slot');

SET @tsql = IF(@texists = 0,
    'ALTER TABLE live_match_totems ADD UNIQUE KEY uk_session_client_slot (session_id, client_id, totem_slot_index)',
    'SELECT "uk_session_client_slot already exists on totems"');
PREPARE tstmt FROM @tsql;
EXECUTE tstmt;
DEALLOCATE PREPARE tstmt;

-- ─────────────────────────────────────────────────────────────────────────────
-- 5. Заполняем created_at = NOW() для строк где оно NULL (старые записи)
-- ─────────────────────────────────────────────────────────────────────────────
UPDATE game_sessions SET created_at = NOW() WHERE created_at IS NULL;

-- ─────────────────────────────────────────────────────────────────────────────
-- 6. Помечаем abandoned висящие waiting-сессии без join_code > 90 сек
-- ─────────────────────────────────────────────────────────────────────────────
UPDATE game_sessions
SET status = 'abandoned'
WHERE status = 'waiting'
  AND join_code IS NULL
  AND TIMESTAMPDIFF(SECOND, created_at, NOW()) > 90;

-- ─────────────────────────────────────────────────────────────────────────────
-- ПРОВЕРКА: итоговое состояние индексов
-- ─────────────────────────────────────────────────────────────────────────────
SELECT
    table_name,
    index_name,
    GROUP_CONCAT(column_name ORDER BY seq_in_index) AS columns,
    IF(non_unique = 0, 'UNIQUE', 'INDEX') AS type
FROM INFORMATION_SCHEMA.STATISTICS
WHERE table_schema = DATABASE()
  AND table_name IN ('game_sessions', 'live_match_weapons', 'live_match_totems')
  AND index_name IN ('idx_sess_created', 'uk_session_client_slot')
GROUP BY table_name, index_name, non_unique
ORDER BY table_name, index_name;
