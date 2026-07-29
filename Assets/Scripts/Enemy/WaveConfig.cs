using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// WaveConfig — ScriptableObject для настройки волн врагов.
///
/// ЗАЧЕМ:
///   Раньше все волны были зашиты в EnemySpawner.CheckEvents() как жёсткий if-код:
///     if (!_waveAt3Min && minutes >= 3f) { SpawnMiniBoss(); ... }
///   При добавлении новой волны нужно было лезть в код.
///   При изменении таймингов — перекомпиляция.
///
/// ТЕПЕРЬ:
///   Все волны настраиваются в Inspector через ScriptableObject.
///   Добавить волну = добавить элемент в список. Без кода.
///
/// КАК СОЗДАТЬ АССЕТ:
///   Assets → правой кнопкой → Create → Game → Wave Config
///   (или через меню Create в любой папке проекта)
///
/// КАК НАСТРОИТЬ:
///   Добавь события в список WaveEvents:
///     TriggerTimeMinutes = 3    → волна в 3 минуты
///     EventType = MiniBoss      → спавним мини-босса
///     EnemyCount = 1            → 1 босс
///     SwarmDurationSeconds = 0  → не рой (только для Swarm типа)
///
/// ПРИМЕР НАСТРОЙКИ (аналог старого CheckEvents):
///   [0] Time=1, Type=EnemyWave,  Count=3, Duration=0
///   [1] Time=3, Type=MiniBoss,   Count=1, Duration=0  + EnemyWave Count=3
///   [2] Time=4, Type=Swarm,      Count=0, Duration=3
///   [3] Time=6, Type=MiniBoss,   Count=1, Duration=0  + EnemyWave Count=3
///   [4] Time=7, Type=EnemyWave,  Count=3, Duration=0
///   [5] Time=8, Type=MiniBoss,   Count=1, Duration=0  + EnemyWave Count=3
///   [6] Time=9, Type=Swarm,      Count=0, Duration=3
/// </summary>
[CreateAssetMenu(fileName = "WaveConfig", menuName = "Game/Wave Config")]
public class WaveConfig : ScriptableObject
{
    [Header("Волновые события")]
    [Tooltip("Список всех событий. Сортируй по времени — EnemySpawner обрабатывает их по порядку.")]
    public List<WaveEvent> WaveEvents = new List<WaveEvent>();
}

/// ─────────────────────────────────────────────────────────────────────────────
/// ТИПЫ СОБЫТИЙ
/// ─────────────────────────────────────────────────────────────────────────────

public enum WaveEventType
{
    /// Спавним N обычных врагов разом
    EnemyWave,

    /// Спавним мини-босса (+ опционально N врагов через EnemyCount)
    MiniBoss,

    /// Активируем рой: двойной спавн на SwarmDurationSeconds секунд
    Swarm,
}

/// ─────────────────────────────────────────────────────────────────────────────
/// ОПИСАНИЕ ОДНОГО ВОЛНОВОГО СОБЫТИЯ
/// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public class WaveEvent
{
    [Tooltip("В какую минуту игры срабатывает событие.")]
    [Min(0)] public float TriggerTimeMinutes;

    [Tooltip("Тип события.")]
    public WaveEventType EventType;

    [Tooltip("Количество врагов (для EnemyWave и доп. врагов при MiniBoss). Для Swarm игнорируется.")]
    [Min(0)] public int EnemyCount = 3;

    [Tooltip("Длительность роя в секундах (только для Swarm). Для остальных типов игнорируется.")]
    [Min(0)] public float SwarmDurationSeconds = 3f;

    // Флаг — событие уже сработало в этой сессии (не сериализуется, сбрасывается при старте)
    [System.NonSerialized] public bool HasFired = false;
}
