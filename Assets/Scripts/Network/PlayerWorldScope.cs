using UnityEngine;
using VContainer;
using VContainer.Unity;

/// <summary>
/// PlayerWorldScope — дочерний DI контейнер для PlayerWorldScene.
///
/// ═══════════════════════════════════════════════════════════════════
/// ЧТО РЕГИСТРИРОВАТЬ ЗДЕСЬ (только per-world):
/// ═══════════════════════════════════════════════════════════════════
///
///   ✅ EnemyPool, EnemyBatchSystem, EnemySpawner, EnemyRespawnTracker
///   ✅ ExperienceOrbPool
///   ✅ ChestSpawner
///   ✅ PropSpawner
///   ✅ GridWorldGenerator (АКТИВНАЯ СИСТЕМА)
///   ✅ GridTerrainAdapter (АКТИВНАЯ СИСТЕМА)
///
/// ═══════════════════════════════════════════════════════════════════
/// ЧТО НЕ РЕГИСТРИРОВАТЬ:
/// ═══════════════════════════════════════════════════════════════════
///
///   ❌ LocalGameTimer — уже в BaseLifetimeScope. Двойная регистрация
///      сбрасывает таймер при загрузке PlayerWorldScene.
///
///   ❌ TotemManager — per-player, не per-world. В Competitive двое игроков
///      → два TotemManager. Инъекция через InjectGameObject при спавне.
///
///   ❌ WeaponManager, CharacterSelectManager — NetworkBehaviour,
///      инъекция через InjectionProvider.Container.InjectGameObject().
///
/// PARENT в Inspector → обязательно BaseLifetimeScope.
/// </summary>
public class PlayerWorldScope : LifetimeScope
{
    [Header("── Pools ────────────────────────────────────────────")]
    [SerializeField] private EnemyPool enemyPool;
    [SerializeField] private ExperienceOrbPool experienceOrbPool;

    [Header("── Spawning ──────────────────────────────────────────")]
    [SerializeField] private EnemySpawner enemySpawner;
    [SerializeField] private EnemyBatchSystem enemyBatchSystem;
    [SerializeField] private EnemyRespawnTracker enemyRespawnTracker;

    [Header("── Loot ─────────────────────────────────────────────")]
    [SerializeField] private ChestSpawner chestSpawner;

    [Header("── World Generation ────────────────────────────────")]
    [Tooltip("Объект WorldGeneration из Hierarchy PlayerWorldScene")]
    [SerializeField] private PropSpawner propSpawner;
    
    [Header("━━━ GRID СИСТЕМА (НОВАЯ) ━━━")]
    [Tooltip("GridWorldGenerator — НОВАЯ система генерации мира")]
    [SerializeField] private GridWorldGenerator gridWorldGenerator;
    [Tooltip("GridTerrainAdapter — адаптер для совместимости")]
    [SerializeField] private GridTerrainAdapter gridTerrainAdapter;

    protected override void Configure(IContainerBuilder builder)
    {
        RegisterRequired(builder, enemyPool,           "EnemyPool");
        RegisterRequired(builder, experienceOrbPool,   "ExperienceOrbPool");
        RegisterRequired(builder, enemySpawner,        "EnemySpawner");
        RegisterRequired(builder, enemyBatchSystem,    "EnemyBatchSystem");
        RegisterRequired(builder, enemyRespawnTracker, "EnemyRespawnTracker");
        RegisterRequired(builder, chestSpawner,        "ChestSpawner");

        if (propSpawner != null)
            builder.RegisterComponent(propSpawner);
        else
            Debug.LogWarning("[PlayerWorldScope] propSpawner не назначен — PropSpawner не будет инжектирован.");

        // ═══════════════════════════════════════════════════════════════════
        // GRID СИСТЕМА (НОВАЯ — единственная активная)
        // ═══════════════════════════════════════════════════════════════════
        if (gridWorldGenerator != null)
        {
            builder.RegisterComponent(gridWorldGenerator);
            Debug.Log("[PlayerWorldScope] ✅ GridWorldGenerator зарегистрирован");
        }
        else
        {
            Debug.LogError("[PlayerWorldScope] ❌ gridWorldGenerator не назначен!");
        }

        if (gridTerrainAdapter != null)
        {
            builder.RegisterComponent(gridTerrainAdapter);
            Debug.Log("[PlayerWorldScope] ✅ GridTerrainAdapter зарегистрирован");
        }
        else
        {
            Debug.LogError("[PlayerWorldScope] ❌ gridTerrainAdapter не назначен!");
        }

        Debug.Log("[PlayerWorldScope] ✅ Configure завершён");
    }

    private void RegisterRequired<T>(IContainerBuilder builder, T component, string name)
        where T : UnityEngine.Component
    {
        if (component != null)
        {
            builder.RegisterComponent(component);
            Debug.Log($"[PlayerWorldScope] ✅ {name} зарегистрирован");
        }
        else
        {
            Debug.LogError($"[PlayerWorldScope] ❌ {name} не назначен в Inspector!");
        }
    }

    void Start()
    {
        InjectionProvider.UpgradeToChildScope(Container);
        Debug.Log("[PlayerWorldScope] ✅ InjectionProvider апгрейднут до PlayerWorldScope");
    }
}
