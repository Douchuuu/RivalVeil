using UnityEngine;

/// <summary>
/// ══════════════════════════════════════════════════════════════════
/// WORLD SEED PROVIDER — единый источник сида для генерации мира
/// ══════════════════════════════════════════════════════════════════
///
/// ЗАЧЕМ:
///   ChestSpawner, TerrainGenerator, PropSpawner и т.д. — каждый
///   создаёт new System.Random(seed + someOffset).
///   Если все используют один и тот же offset=0, их последовательности
///   совпадают → объекты разных типов кластеризуются в одних местах.
///
///   WorldSeedProvider выдаёт каждой системе уникальный System.Random
///   через GetRandom(channel), где channel — уникальная строка.
///   Внутри: hash строки + BaseSeed → уникальный int сид на систему.
///
/// КАНАЛЫ (channel):
///   "terrain"   → TerrainGenerator
///   "props"     → PropSpawner (камни, деревья, заборы)
///   "chests"    → ChestSpawner
///   "enemies"   → EnemySpawner (точки спавна)
///   "biomes"    → BiomeMap (если будет)
///   Любая строка — просто добавь новый канал.
///
/// ИНТЕГРАЦИЯ:
///   1. Зарегистрируй в BaseLifetimeScope:
///      builder.Register<WorldSeedProvider>(Lifetime.Singleton);
///   2. Инжектируй через [Inject]:
///      [Inject] private WorldSeedProvider _seedProvider;
///   3. Получай Random:
///      var rng = _seedProvider.GetRandom("terrain");
///
/// SEED SOURCE:
///   - ShadowMultiplayer → GameModeManager.SharedSeed (синхронизирован сервером)
///   - SinglePlayer      → случайный seed, генерируется один раз в Initialize()
///
/// ВАЖНО: GetRandom() каждый раз создаёт НОВЫЙ System.Random с тем же сидом.
///   Это детерминировано — порядок вызовов внутри одного генератора
///   должен быть стабильным. Не вызывай GetRandom() в середине генерации.
/// </summary>
public class WorldSeedProvider
{
    // ─── СОСТОЯНИЕ ────────────────────────────────────────────────────────────

    private int _baseSeed;
    private bool _initialized;

    /// <summary>Текущий базовый сид. Только для отображения/отладки.</summary>
    public int BaseSeed => _baseSeed;

    // ─── ИНИЦИАЛИЗАЦИЯ ────────────────────────────────────────────────────────

    /// <summary>
    /// Вызывается один раз при старте игры (из WorldSceneLoader или аналога).
    /// После вызова BaseSeed зафиксирован на всю игровую сессию.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
        {
            Debug.LogWarning("[WorldSeedProvider] Already initialized, skipping.");
            return;
        }

        if (GameModeManager.IsCompetitiveMode() && GameModeManager.SharedSeed != 0)
        {
            // ShadowMultiplayer: сид синхронизирован сервером — оба игрока
            // получат одинаковый мир (одни позиции объектов, один рельеф).
            _baseSeed = GameModeManager.SharedSeed;
            Debug.Log($"[WorldSeedProvider] COMPETITIVE: seed={_baseSeed}");
        }
        else
        {
            // SinglePlayer: генерируем уникальный сид для этой сессии.
            _baseSeed = Random.Range(1, int.MaxValue);
            Debug.Log($"[WorldSeedProvider] SINGLEPLAYER: seed={_baseSeed}");
        }

        _initialized = true;
    }

    // ─── ПОЛУЧЕНИЕ RANDOM ──────────────────────────────────────────────────────

    /// <summary>
    /// Возвращает детерминированный System.Random для конкретного канала генерации.
    /// Каждый вызов с одним channel возвращает ОДИНАКОВУЮ начальную последовательность.
    ///
    /// Используй один раз в начале генерации — сохрани как поле, не вызывай повторно
    /// в середине генерации (это сбросит последовательность).
    /// </summary>
    public System.Random GetRandom(string channel)
    {
        EnsureInitialized();
        int channelSeed = _baseSeed ^ GetChannelHash(channel);
        return new System.Random(channelSeed);
    }

    /// <summary>
    /// Возвращает числовой сид для конкретного канала.
    /// Используй для Perlin Noise offset или Unity.Mathematics.noise.
    /// </summary>
    public float GetNoiseOffset(string channel)
    {
        EnsureInitialized();
        // Нормализуем в [0, 1000] — удобный диапазон для Perlin offset
        return Mathf.Abs((_baseSeed ^ GetChannelHash(channel)) % 1000) / 1.0f;
    }

    // ─── ВСПОМОГАТЕЛЬНОЕ ──────────────────────────────────────────────────────

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            Debug.LogWarning("[WorldSeedProvider] Not initialized yet. Calling Initialize().");
            Initialize();
        }
    }

    /// <summary>
    /// Детерминированный хэш строки (не использует GetHashCode — он нестабилен между
    /// сессиями в .NET 5+). Алгоритм: djb2.
    /// </summary>
    private static int GetChannelHash(string channel)
    {
        int hash = 5381;
        foreach (char c in channel)
            hash = ((hash << 5) + hash) ^ c;
        return hash;
    }

    /// <summary>
    /// Сброс состояния — для тестов и перезапуска сессии.
    /// </summary>
    public void Reset()
    {
        _baseSeed = 0;
        _initialized = false;
    }
}
