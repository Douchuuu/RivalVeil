using UnityEngine;
using VContainer;
using VContainer.Unity;

/// <summary>
/// InjectionProvider v1.1 — глобальный провайдер VContainer DI.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНО (v1.1):
/// ══════════════════════════════════════════════════════════════════════
///   - Добавлено подробное логирование для диагностики.
///   - Улучшены проверки на null.
///   - Добавлены методы для работы с дочерними скоупами.
/// ══════════════════════════════════════════════════════════════════════
///
/// ══════════════════════════════════════════════════════════════════════
/// ЗАЧЕМ НУЖЕН:
/// ══════════════════════════════════════════════════════════════════════
///
///   VContainer умеет инжектить зависимости только в объекты, которые
///   он сам создаёт. В проекте два класса объектов обходят VContainer:
///
///   1. NetworkBehaviour (PlayerStats, WeaponManager, DisconnectHandler)
///      → спавнит Netcode, VContainer не участвует → [Inject] = null
///
///   2. Объекты из пулов (EnemyHealth, SwordWeapon, AuraWeapon)
///      → создаются через Instantiate() → [Inject] = null
///
/// ══════════════════════════════════════════════════════════════════════
/// КАК РЕШАЕТ ПРОБЛЕМУ:
/// ══════════════════════════════════════════════════════════════════════
///
///   InjectionProvider — MonoBehaviour, которого VContainer создаёт
///   НОРМАЛЬНО (он на сцене, зарегистрирован в GameLifetimeScope).
///   VContainer инжектит в него IObjectResolver и сохраняет статически.
///
///   После этого любой объект может вызвать:
///     InjectionProvider.Container.InjectGameObject(this.gameObject);
///   ...и VContainer пройдёт по компонентам объекта, найдёт все
///   [Inject]-методы и заполнит их зависимостями.
///
/// ══════════════════════════════════════════════════════════════════════
/// ГАРАНТИЯ ПОРЯДКА:
/// ══════════════════════════════════════════════════════════════════════
///
///   BaseLifetimeScope → Awake → Configure() → регистрирует InjectionProvider
///   → VContainer вызывает Construct() → Container = BaseScope установлен
///   → PlayerWorldScope загружается → Start() → UpgradeToChildScope()
///   → Container = PlayerWorldScope (знает EnemyPool + всё из BaseScope)
///   → WeaponManager.UnlockWeapon() → InjectGameObject → EnemyPool найден ✅
///
/// ══════════════════════════════════════════════════════════════════════
/// РАЗМЕЩЕНИЕ:
/// ══════════════════════════════════════════════════════════════════════
///
///   Один GameObject [InjectionProvider] на сцене рядом с BaseLifetimeScope.
///   Назначить в Inspector поле injectionProvider в BaseLifetimeScope.
/// </summary>
public class InjectionProvider : MonoBehaviour
{
    /// <summary>
    /// Текущий активный контейнер VContainer.
    /// </summary>
    public static IObjectResolver Container { get; private set; }

    /// <summary>
    /// Родительский контейнер (из BaseLifetimeScope).
    /// </summary>
    public static IObjectResolver ParentContainer { get; private set; }

    /// <summary>
    /// Дочерний контейнер (из PlayerWorldScope).
    /// </summary>
    public static IObjectResolver ChildContainer { get; private set; }

    /// <summary>
    /// Установить родительский контейнер (вызывается из BaseLifetimeScope).
    /// </summary>
    public static void SetContainer(IObjectResolver container)
    {
        Container = container;
        ParentContainer = container;
        Debug.Log("[InjectionProvider] ✅ Родительский контейнер установлен.");
    }

    /// <summary>
    /// Апгрейд до дочернего контейнера (вызывается из PlayerWorldScope).
    /// </summary>
    public static void UpgradeToChildScope(IObjectResolver childContainer)
    {
        ChildContainer = childContainer;
        Container = childContainer;
        Debug.Log("[InjectionProvider] ✅ Контейнер апгрейднут до дочернего (PlayerWorldScope).");
    }

    /// <summary>
    /// Сбросить на родительский контейнер (вызывается при выгрузке PlayerWorldScene).
    /// </summary>
    public static void DowngradeToParentScope()
    {
        if (ParentContainer != null)
        {
            Container = ParentContainer;
            ChildContainer = null;
            Debug.Log("[InjectionProvider] ✅ Контейнер сброшен до родительского.");
        }
    }

    /// <summary>
    /// Проверить, установлен ли контейнер.
    /// </summary>
    public static bool IsContainerReady => Container != null;

    /// <summary>
    /// Получить сервис из контейнера.
    /// </summary>
    public static T Resolve<T>() where T : class
    {
        if (Container == null)
        {
            Debug.LogError("[InjectionProvider] ❌ Контейнер не установлен!");
            return null;
        }

        try
        {
            return Container.Resolve<T>();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[InjectionProvider] ❌ Не удалось resolve {typeof(T).Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Попробовать получить сервис из контейнера (без ошибки если не найден).
    /// </summary>
    public static bool TryResolve<T>(out T service) where T : class
    {
        service = null;
        
        if (Container == null)
            return false;

        try
        {
            service = Container.Resolve<T>();
            return service != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Зарегистрировать объект в контейнере (для runtime-регистрации).
    /// </summary>
    public static void Register<T>(T instance) where T : class
    {
        if (Container == null)
        {
            Debug.LogError("[InjectionProvider] ❌ Контейнер не установлен!");
            return;
        }

        Debug.Log($"[InjectionProvider] Регистрация {typeof(T).Name} в контейнере.");
        // Примечание: VContainer не поддерживает runtime-регистрацию напрямую.
        // Этот метод для совместимости, в реальности нужно использовать другой подход.
    }

    /// <summary>
    /// Выполнить инъекцию в GameObject.
    /// </summary>
    public static void InjectGameObject(GameObject gameObject)
    {
        if (Container == null)
        {
            Debug.LogWarning("[InjectionProvider] ⚠️ Контейнер не установлен, инъекция пропущена.");
            return;
        }

        if (gameObject == null)
        {
            Debug.LogWarning("[InjectionProvider] ⚠️ gameObject == null, инъекция пропущена.");
            return;
        }

        try
        {
            Container.InjectGameObject(gameObject);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[InjectionProvider] ❌ Ошибка инъекции в {gameObject.name}: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VContainer INJECTION
    // ─────────────────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(IObjectResolver resolver)
    {
        SetContainer(resolver);
    }
}
/*using UnityEngine;
using VContainer;
using VContainer.Unity;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════
/// INJECTION PROVIDER — центральная точка доступа к VContainer
/// ══════════════════════════════════════════════════════════════════════
///
/// ЗАЧЕМ НУЖЕН:
///   VContainer умеет инжектить зависимости только в объекты, которые
///   он сам создаёт. В проекте два класса объектов обходят VContainer:
///
///   1. NetworkBehaviour (PlayerStats, WeaponManager, DisconnectHandler)
///      → спавнит Netcode, VContainer не участвует → [Inject] = null
///
///   2. Объекты из пулов (EnemyHealth, SwordWeapon, AuraWeapon)
///      → создаются через Instantiate() → [Inject] = null
///
/// КАК РЕШАЕТ ПРОБЛЕМУ:
///   InjectionProvider — MonoBehaviour, которого VContainer создаёт
///   НОРМАЛЬНО (он на сцене, зарегистрирован в GameLifetimeScope).
///   VContainer инжектит в него IObjectResolver и сохраняет статически.
///
///   После этого любой объект может вызвать:
///     InjectionProvider.Container.InjectGameObject(this.gameObject);
///   ...и VContainer пройдёт по компонентам объекта, найдёт все
///   [Inject]-методы и заполнит их зависимостями.
///
/// ГАРАНТИЯ ПОРЯДКА:
///   BaseLifetimeScope → Awake → Configure() → регистрирует InjectionProvider
///   → VContainer вызывает Construct() → Container = BaseScope установлен
///   → PlayerWorldScope загружается → Start() → UpgradeToChildScope()
///   → Container = PlayerWorldScope (знает EnemyPool + всё из BaseScope)
///   → WeaponManager.UnlockWeapon() → InjectGameObject → EnemyPool найден ✅
///
/// РАЗМЕЩЕНИЕ:
///   Один GameObject [InjectionProvider] на сцене рядом с BaseLifetimeScope.
///   Назначить в Inspector поле injectionProvider в BaseLifetimeScope.
/// </summary>
public class InjectionProvider : MonoBehaviour
{
    /// <summary>
    /// Глобально доступный VContainer-контейнер.
    /// Сначала = BaseLifetimeScope, затем апгрейдится до PlayerWorldScope.
    /// Сбрасывается в null при уничтожении объекта (смена сцены).
    /// </summary>
    public static IObjectResolver Container { get; private set; }

    [Inject]
    public void Construct(IObjectResolver resolver)
    {
        Container = resolver;
        Debug.Log("[InjectionProvider] ✅ Container установлен (BaseLifetimeScope)");
    }

    /// <summary>
    /// Вызывается из PlayerWorldScope.Start() после построения дочернего контейнера.
    /// Дочерний контейнер видит ВСЁ: и своё (EnemyPool), и родительское (CombatCalculator).
    /// После этого WeaponManager.UnlockWeapon может инжектить SpeedStaffWeapon корректно.
    /// </summary>
    public static void UpgradeToChildScope(IObjectResolver childResolver)
    {
        Container = childResolver;
        Debug.Log("[InjectionProvider] ✅ Container апгрейднут до PlayerWorldScope — EnemyPool доступен");
    }

    void OnDestroy()
    {
        Container = null;
        Debug.Log("[InjectionProvider] Container сброшен (смена сцены)");
    }
}
*/  