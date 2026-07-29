using UnityEngine;
using VContainer;
using VContainer.Unity;

/// <summary>
/// BaseLifetimeScope — родительский DI контейнер.
///
/// ИСПРАВЛЕНО:
///   GameStateService и CombatCalculator — plain C# классы, не MonoBehaviour.
///   Использовать builder.Register<T>(Lifetime.Singleton), не RegisterComponent/RegisterService.
///   SerializeField не имеет смысла для plain C# классов.
/// </summary>
public class BaseLifetimeScope : LifetimeScope
{
    [Header("=== Databases (ScriptableObjects) ===")]
    [SerializeField] private CharacterDatabase characterDatabase;
    [SerializeField] private WeaponDatabase weaponDatabase;
    [SerializeField] private TotemDatabase totemDatabase;
    [SerializeField] private ItemDatabase itemDatabase;

    [Header("=== MonoBehaviour Services ===")]
    [SerializeField] private KillValidator killValidator;
    [SerializeField] private DisconnectHandler disconnectHandler;

    [Header("=== UI ===")]
    [SerializeField] private CharacterSelectUI characterSelectUI;
    [SerializeField] private CompetitiveUIManager_Canvas competitiveUIManager;

    [Header("=== Managers ===")]
    [SerializeField] private LevelUpManager levelUpManager;
    [SerializeField] private ShadowWorldManager shadowWorldManager;
    [SerializeField] private SpectatorManager spectatorManager;
    [SerializeField] private GameOverScreenManager gameOverScreenManager;

    [Header("=== Infrastructure ===")]
    [SerializeField] private InjectionProvider injectionProvider;
    [SerializeField] private WorldSceneLoader worldSceneLoader;
    [SerializeField] private LocalGameTimer localGameTimer;

    protected override void Configure(IContainerBuilder builder)
    {
        Debug.Log("[BaseLifetimeScope] Configure...");

        // ── ScriptableObjects — RegisterInstance ──────────────────────────────
        if (characterDatabase != null)
            builder.RegisterInstance(characterDatabase);
        else
            Debug.LogWarning("[BaseLifetimeScope] ⚠️ CharacterDatabase не назначен!");

        if (weaponDatabase != null)
            builder.RegisterInstance(weaponDatabase);
        else
            Debug.LogError("[BaseLifetimeScope] ❌ WeaponDatabase не назначен!");

        if (totemDatabase != null)
            builder.RegisterInstance(totemDatabase);
        else
            Debug.LogWarning("[BaseLifetimeScope] ⚠️ TotemDatabase не назначен!");

        if (itemDatabase != null)
            builder.RegisterInstance(itemDatabase);

        // ── Plain C# Singletons — Register<T>(Lifetime.Singleton) ────────────
        // GameStateService и CombatCalculator НЕ MonoBehaviour — нельзя RegisterComponent!
        builder.Register<GameStateService>(Lifetime.Singleton);
        builder.Register<CombatCalculator>(Lifetime.Singleton);
        builder.Register<RatingService>(Lifetime.Singleton);
        builder.Register<PlayerRegistry>(Lifetime.Singleton);
        builder.Register<WorldSeedProvider>(Lifetime.Singleton);

        // ── MonoBehaviour Components — RegisterComponent ──────────────────────
        if (injectionProvider != null)
            builder.RegisterComponent(injectionProvider);
        else
            Debug.LogError("[BaseLifetimeScope] ❌ InjectionProvider не назначен!");

        if (worldSceneLoader != null)
            builder.RegisterComponent(worldSceneLoader);

        if (localGameTimer != null)
            builder.RegisterComponent(localGameTimer);

        if (levelUpManager != null)
            builder.RegisterComponent(levelUpManager);
        else
            Debug.LogWarning("[BaseLifetimeScope] ⚠️ LevelUpManager не назначен!");

        if (shadowWorldManager != null)
            builder.RegisterComponent(shadowWorldManager);

        if (spectatorManager != null)
            builder.RegisterComponent(spectatorManager);

        if (gameOverScreenManager != null)
            builder.RegisterComponent(gameOverScreenManager);

        if (killValidator != null)
            builder.RegisterComponent(killValidator);

        if (disconnectHandler != null)
            builder.RegisterComponent(disconnectHandler);

        // ── UI ────────────────────────────────────────────────────────────────
        if (characterSelectUI != null)
            builder.RegisterComponent(characterSelectUI);

        if (competitiveUIManager != null)
            builder.RegisterComponent(competitiveUIManager);

        Debug.Log("[BaseLifetimeScope] ✅ Configure завершён");
    }

    protected override void Awake()
    {
        base.Awake();
        if (transform.parent == null)
        {
            DontDestroyOnLoad(gameObject);
            Debug.Log("[BaseLifetimeScope] DontDestroyOnLoad установлен");
        }
    }
}
