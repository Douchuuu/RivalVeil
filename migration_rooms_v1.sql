-- ═══════════════════════════════════════════════════════════════════════════════
-- MIGRATION rooms_v1 — поддержка приватных комнат (rooms_v2 patch)
--
-- Применять ПОСЛЕ migration_v5.sql.
-- Добавляет:
--   rooms.created_at   — для автоэкспайри (пустые >5 мин, любые >20 мин)
--   rooms.guest_id     — кто присоединился (для логики "только хост меняет relay")
--   matches.is_room_match — комнатный матч без изменения ELO
-- ═══════════════════════════════════════════════════════════════════════════════

USE rivalveil;

-- ─── 1. created_at в rooms (для экспайри) ────────────────────────────────────
SET @exists = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
               WHERE table_schema = DATABASE()
               AND table_name = 'rooms'
               AND column_name = 'created_at');

SET @sql = IF(@exists = 0,
    'ALTER TABLE rooms ADD COLUMN created_at DATETIME DEFAULT NOW()',
    'SELECT "created_at already exists in rooms"');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- ─── 2. guest_id в rooms (кто присоединился) ─────────────────────────────────
SET @exists2 = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE table_schema = DATABASE()
                AND table_name = 'rooms'
                AND column_name = 'guest_id');

SET @sql2 = IF(@exists2 = 0,
    'ALTER TABLE rooms ADD COLUMN guest_id INT DEFAULT NULL',
    'SELECT "guest_id already exists"');
PREPARE stmt2 FROM @sql2;
EXECUTE stmt2;
DEALLOCATE PREPARE stmt2;

-- ─── 3. is_room_match в matches (без изменения ELO) ──────────────────────────
SET @exists3 = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE table_schema = DATABASE()
                AND table_name = 'matches'
                AND column_name = 'is_room_match');

SET @sql3 = IF(@exists3 = 0,
    'ALTER TABLE matches ADD COLUMN is_room_match TINYINT(1) DEFAULT 0',
    'SELECT "is_room_match already exists"');
PREPARE stmt3 FROM @sql3;
EXECUTE stmt3;
DEALLOCATE PREPARE stmt3;

-- ─── 4. Заполняем created_at = NOW() для старых записей ──────────────────────
UPDATE rooms SET created_at = NOW() WHERE created_at IS NULL;

-- ─── 5. Удаляем протухшие открытые комнаты (старше 20 минут) ─────────────────
DELETE FROM rooms
WHERE is_open = 1
  AND created_at IS NOT NULL
  AND TIMESTAMPDIFF(MINUTE, created_at, NOW()) > 20;

-- ─── ПРОВЕРКА ────────────────────────────────────────────────────────────────
SELECT column_name, column_type, column_default
FROM INFORMATION_SCHEMA.COLUMNS
WHERE table_schema = DATABASE()
  AND table_name IN ('rooms', 'matches')
  AND column_name IN ('created_at', 'guest_id', 'is_room_match')
ORDER BY table_name, column_name;
