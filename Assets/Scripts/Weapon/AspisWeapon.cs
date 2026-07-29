using UnityEngine;
using Unity.Netcode;
using VContainer;
using System.Collections.Generic;

// ─────────────────────────────────────────────────────────────────────────────
// ИНТЕРФЕЙС ДЛЯ ПЕРЕХВАТА УРОНА
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Интерфейс щита-перехватчика урона.
/// PlayerStats.TakeDamage() вызывает InterceptDamage() до расчёта финального урона.
/// </summary>
public interface IShieldInterceptor
{
    float InterceptDamage(float rawDamage, EnemyHealth attacker);
}

// ─────────────────────────────────────────────────────────────────────────────
// ОРУЖИЕ: ЩИТ (АСПИС) v3
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// AspisWeapon v3 — убран legacy IUpgradeable, корректные StatBonusType.
///
/// Единственный путь апгрейда: IDynamicUpgradeable.ApplyDynamicUpgrade()
///
/// Поддерживаемые статы:
///   ShieldCharge   — +N зарядов щита (отображается "+1 заряд(ов) щита")
///   ShieldRegen    — % ускорение регенерации заряда ("скорость восстановления")
///   CritChance     — + абс. шанс крита рикошета
///   CritMultiplier — + множитель крита рикошета
///
/// Эффекты:
///   • -70% урона при наличии заряда
///   • 30% от принятого урона как рикошет (с шансом крита)
///   • +25 HP пока оружие одето
/// </summary>
public class AspisWeapon : MonoBehaviour, IDynamicUpgradeable, IShieldInterceptor
{
    [Header("🛡️ БАЗОВЫЕ ХАРАКТЕРИСТИКИ ЩИТА")]
    [SerializeField] private int baseShieldCount = 1;
    [SerializeField] private float baseRegenCooldown = 3f;
    [SerializeField] private float baseCritChance = 0f;
    [SerializeField] private float baseCritMultiplier = 2f;

    [Header("🌀 ВИЗУАЛ ОРБИТЫ")]
    [SerializeField] private float orbitRadius = 1.5f;
    [SerializeField] private float orbitSpeed = 60f;
    [SerializeField] private GameObject shieldSegmentPrefab;
    [SerializeField] private Vector3 segmentScale = new Vector3(0.3f, 0.6f, 0.1f);

    private const float DAMAGE_REDUCTION = 0.70f;
    private const float RETALIATION_FRACTION = 0.30f;
    private const float HEALTH_BONUS = 25f;

    // ── Текущие характеристики ────────────────────────────────────────────────
    private int _currentMaxCharges;
    private float _currentRegenCooldown;
    private float _currentCritChance;
    private float _currentCritMultiplier;

    // ── Накопленные бонусы ────────────────────────────────────────────────────
    private int _bonusExtraCharges = 0;
    private float _bonusRegenSpeedPct = 0f;
    private float _bonusCritChanceAbs = 0f;
    private float _bonusCritMultiplierAbs = 0f;

    // ── Состояние ─────────────────────────────────────────────────────────────
    private int _currentCharges;
    private float _regenTimer;
    private float _orbitAngle;

    private readonly List<GameObject> _shieldVisuals = new List<GameObject>();

    private PlayerStats _playerStats;
    private NetworkBehaviour _playerNetwork;
    private CombatCalculator _combatCalc;

    // ─── INJECTION ────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(CombatCalculator combatCalc)
    {
        _combatCalc = combatCalc;
        Debug.Log("[AspisWeapon] ✅ CombatCalculator инжектирован");
    }

    void Awake() => RecalcStats(rebuildVisuals: false);

    void Start()
    {
        _playerStats = GetComponentInParent<PlayerStats>();
        _playerNetwork = GetComponentInParent<NetworkBehaviour>();

        if (_playerStats != null)
        {
            _playerStats.RegisterShield(this);
            _playerStats.AddBonusMaxHealth(HEALTH_BONUS);
        }
        else
        {
            Debug.LogWarning("[AspisWeapon] PlayerStats не найден у родителя!");
        }

        RebuildShieldVisuals();
        Debug.Log($"[AspisWeapon] Init: charges={_currentCharges}/{_currentMaxCharges}, " +
                  $"regen={_currentRegenCooldown:F1}s, crit={_currentCritChance * 100f:F0}%×{_currentCritMultiplier:F1}");
    }

    void OnDestroy()
    {
        if (_playerStats != null)
        {
            _playerStats.UnregisterShield(this);
            _playerStats.RemoveBonusMaxHealth(HEALTH_BONUS);
        }
        DestroyShieldVisuals();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПЕРЕСЧЁТ ХАРАКТЕРИСТИК
    // ─────────────────────────────────────────────────────────────────────────

    void RecalcStats(bool rebuildVisuals = true)
    {
        int newMax = baseShieldCount + _bonusExtraCharges;
        bool chargesChanged = newMax != _currentMaxCharges;

        if (chargesChanged)
        {
            int added = newMax - _currentMaxCharges;
            _currentMaxCharges = newMax;
            if (added > 0)
                _currentCharges = Mathf.Min(_currentCharges + added, _currentMaxCharges);
            if (_currentCharges == 0 && _currentMaxCharges > 0)
                _currentCharges = _currentMaxCharges;
        }

        _currentRegenCooldown = baseRegenCooldown / (1f + _bonusRegenSpeedPct / 100f);
        // Overflow крит.шанса → бонус крит.урона (та же формула что в PlayerStatSheet)
        float rawCritChance = baseCritChance + _bonusCritChanceAbs;
        _currentCritChance = Mathf.Clamp01(rawCritChance);
        float critOverflow = Mathf.Max(0f, rawCritChance - 1f);
        float overflowMult = critOverflow > 0f ? Mathf.Sqrt(critOverflow) * 0.5f : 0f;
        _currentCritMultiplier = baseCritMultiplier + _bonusCritMultiplierAbs + overflowMult;

        if (rebuildVisuals && chargesChanged)
            RebuildShieldVisuals();
        else if (rebuildVisuals)
            UpdateShieldActiveState();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE — ОРБИТА + ВОССТАНОВЛЕНИЕ
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        _orbitAngle = (_orbitAngle + orbitSpeed * Time.deltaTime) % 360f;
        UpdateShieldPositions();

        if (!IsLocalPlayer()) return;
        TickRegenTimer();
    }

    private void TickRegenTimer()
    {
        if (_currentCharges >= _currentMaxCharges) return;

        _regenTimer -= Time.deltaTime;
        if (_regenTimer <= 0f)
        {
            _currentCharges = Mathf.Min(_currentCharges + 1, _currentMaxCharges);
            UpdateShieldActiveState();
            Debug.Log($"[AspisWeapon] ♻️ Заряд восстановлен! {_currentCharges}/{_currentMaxCharges}");

            if (_currentCharges < _currentMaxCharges)
                _regenTimer = _currentRegenCooldown;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IShieldInterceptor
    // ─────────────────────────────────────────────────────────────────────────

    public float InterceptDamage(float rawDamage, EnemyHealth attacker)
    {
        if (_currentCharges <= 0) return rawDamage;

        ConsumeCharge();

        float reducedDamage = rawDamage * (1f - DAMAGE_REDUCTION);

        if (attacker != null)
        {
            float retaliationBase = rawDamage * RETALIATION_FRACTION;
            float finalRetaliation = CalculateRetaliationDamage(retaliationBase);
            attacker.TakeDamage(finalRetaliation, _playerStats);
        }

        Debug.Log($"[AspisWeapon] 🛡️ Щит! {rawDamage:F1}→{reducedDamage:F1}, " +
                  $"заряды={_currentCharges}/{_currentMaxCharges}");
        return reducedDamage;
    }

    private void ConsumeCharge()
    {
        bool wasFull = (_currentCharges == _currentMaxCharges);
        _currentCharges = Mathf.Max(0, _currentCharges - 1);
        UpdateShieldActiveState();
        if (wasFull) _regenTimer = _currentRegenCooldown;
    }

    private float CalculateRetaliationDamage(float baseDmg)
    {
        if (_currentCritChance > 0f && Random.value < _currentCritChance)
        {
            float critDmg = baseDmg * _currentCritMultiplier;
            Debug.Log($"[AspisWeapon] ✨ КРИТ! {baseDmg:F1}→{critDmg:F1} (×{_currentCritMultiplier:F1})");
            return critDmg;
        }
        return baseDmg;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ
    // ─────────────────────────────────────────────────────────────────────────

    private void RebuildShieldVisuals()
    {
        DestroyShieldVisuals();

        for (int i = 0; i < _currentMaxCharges; i++)
        {
            GameObject seg;
            if (shieldSegmentPrefab != null)
            {
                seg = Instantiate(shieldSegmentPrefab, Vector3.zero, Quaternion.identity, null);
            }
            else
            {
                seg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                seg.transform.localScale = segmentScale;
                var col = seg.GetComponent<Collider>();
                if (col != null) Destroy(col);
                var rend = seg.GetComponent<Renderer>();
                if (rend != null) rend.material.color = new Color(0.2f, 0.5f, 1f, 0.85f);
            }

            seg.name = $"ShieldSegment_{i}";
            _shieldVisuals.Add(seg);
        }

        UpdateShieldActiveState();
        UpdateShieldPositions();
    }

    private void DestroyShieldVisuals()
    {
        foreach (var vis in _shieldVisuals)
            if (vis != null) Destroy(vis);
        _shieldVisuals.Clear();
    }

    private void UpdateShieldPositions()
    {
        int count = _shieldVisuals.Count;
        if (count == 0) return;

        Vector3 center = transform.parent != null ? transform.parent.position : transform.position;
        float step = 360f / count;

        for (int i = 0; i < count; i++)
        {
            if (_shieldVisuals[i] == null) continue;
            float rad = (_orbitAngle + step * i) * Mathf.Deg2Rad;
            _shieldVisuals[i].transform.position = new Vector3(
                center.x + Mathf.Cos(rad) * orbitRadius,
                center.y + 0.5f,
                center.z + Mathf.Sin(rad) * orbitRadius);
        }
    }

    private void UpdateShieldActiveState()
    {
        for (int i = 0; i < _shieldVisuals.Count; i++)
        {
            if (_shieldVisuals[i] != null)
                _shieldVisuals[i].SetActive(i < _currentCharges);
        }
    }

    private bool IsLocalPlayer()
    {
        // Dedicated Server не является локальным игроком
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && !NetworkManager.Singleton.IsHost) return false;
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return _playerStats != null;
        return _playerNetwork != null && _playerNetwork.IsOwner;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDynamicUpgradeable
    // ─────────────────────────────────────────────────────────────────────────

    public float? GetCurrentStatValue(StatBonusType stat)
    {
        switch (stat)
        {
            case StatBonusType.ShieldCharge: return _currentMaxCharges;
            case StatBonusType.ShieldRegen: return _currentRegenCooldown;
            case StatBonusType.CritChance: return _currentCritChance * 100f;
            case StatBonusType.CritMultiplier: return _currentCritMultiplier;
            default: return null;
        }
    }

    public StatBonusType[] GetAvailableStats() => new[]
    {
        StatBonusType.ShieldCharge,    // "+1 заряд(ов) щита"
        StatBonusType.ShieldRegen,     // "+X% скорость восстановления щита"
        StatBonusType.CritChance,
        StatBonusType.CritMultiplier,
    };

    public void ApplyDynamicUpgrade(WeaponUpgradeOffer offer)
    {
        Debug.Log($"⬆️ [AspisWeapon] {offer.Rarity} — ДО: " +
                  $"charges={_currentMaxCharges}, regen={_currentRegenCooldown:F2}s");

        foreach (var bonus in offer.Bonuses)
        {
            switch (bonus.type)
            {
                case StatBonusType.ShieldCharge:
                    _bonusExtraCharges += (int)bonus.value; break;
                case StatBonusType.ShieldRegen:
                    _bonusRegenSpeedPct += bonus.value; break;
                case StatBonusType.CritChance:
                    _bonusCritChanceAbs += bonus.value / 100f; break;
                case StatBonusType.CritMultiplier:
                    _bonusCritMultiplierAbs += bonus.value; break;
            }
        }

        RecalcStats(rebuildVisuals: true);

        Debug.Log($"⬆️ [AspisWeapon] Готово — charges={_currentMaxCharges}, regen={_currentRegenCooldown:F2}s");
    }

    // ─────────────────────────────────────────────────────────────────────────

    public int GetCurrentCharges() => _currentCharges;
    public int GetMaxCharges() => _currentMaxCharges;
    public float GetRegenCooldown() => _currentRegenCooldown;
    public float GetRegenProgress() => (_currentCharges < _currentMaxCharges && _currentRegenCooldown > 0f)
                                         ? 1f - (_regenTimer / _currentRegenCooldown) : 1f;
    public float GetCritChance() => _currentCritChance;
    public float GetCritMultiplier() => _currentCritMultiplier;

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.5f, 1f, 0.4f);
        Gizmos.DrawWireSphere(transform.position, orbitRadius);
    }
#endif
}
/*using UnityEngine;
using Unity.Netcode;
using VContainer;
using System.Collections.Generic;

// ─────────────────────────────────────────────────────────────────────────────
// ИНТЕРФЕЙС ДЛЯ ПЕРЕХВАТА УРОНА
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Интерфейс щита-перехватчика урона.
/// PlayerStats.TakeDamage() вызывает InterceptDamage() до расчёта финального урона.
/// </summary>
public interface IShieldInterceptor
{
    float InterceptDamage(float rawDamage, EnemyHealth attacker);
}

// ─────────────────────────────────────────────────────────────────────────────
// ОРУЖИЕ: ЩИТ (АСПИС) v3
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// AspisWeapon v3 — убран legacy IUpgradeable, корректные StatBonusType.
///
/// Единственный путь апгрейда: IDynamicUpgradeable.ApplyDynamicUpgrade()
///
/// Поддерживаемые статы:
///   ShieldCharge   — +N зарядов щита (отображается "+1 заряд(ов) щита")
///   ShieldRegen    — % ускорение регенерации заряда ("скорость восстановления")
///   CritChance     — + абс. шанс крита рикошета
///   CritMultiplier — + множитель крита рикошета
///
/// Эффекты:
///   • -70% урона при наличии заряда
///   • 30% от принятого урона как рикошет (с шансом крита)
///   • +25 HP пока оружие одето
/// </summary>
public class AspisWeapon : MonoBehaviour, IDynamicUpgradeable, IShieldInterceptor
{
    [Header("🛡️ БАЗОВЫЕ ХАРАКТЕРИСТИКИ ЩИТА")]
    [SerializeField] private int baseShieldCount = 1;
    [SerializeField] private float baseRegenCooldown = 3f;
    [SerializeField] private float baseCritChance = 0f;
    [SerializeField] private float baseCritMultiplier = 2f;

    [Header("🌀 ВИЗУАЛ ОРБИТЫ")]
    [SerializeField] private float orbitRadius = 1.5f;
    [SerializeField] private float orbitSpeed = 60f;
    [SerializeField] private GameObject shieldSegmentPrefab;
    [SerializeField] private Vector3 segmentScale = new Vector3(0.3f, 0.6f, 0.1f);

    private const float DAMAGE_REDUCTION = 0.70f;
    private const float RETALIATION_FRACTION = 0.30f;
    private const float HEALTH_BONUS = 25f;

    // ── Текущие характеристики ────────────────────────────────────────────────
    private int _currentMaxCharges;
    private float _currentRegenCooldown;
    private float _currentCritChance;
    private float _currentCritMultiplier;

    // ── Накопленные бонусы ────────────────────────────────────────────────────
    private int _bonusExtraCharges = 0;
    private float _bonusRegenSpeedPct = 0f;
    private float _bonusCritChanceAbs = 0f;
    private float _bonusCritMultiplierAbs = 0f;

    // ── Состояние ─────────────────────────────────────────────────────────────
    private int _currentCharges;
    private float _regenTimer;
    private float _orbitAngle;

    private readonly List<GameObject> _shieldVisuals = new List<GameObject>();

    private PlayerStats _playerStats;
    private NetworkBehaviour _playerNetwork;
    private CombatCalculator _combatCalc;

    // ─── INJECTION ────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(CombatCalculator combatCalc)
    {
        _combatCalc = combatCalc;
        Debug.Log("[AspisWeapon] ✅ CombatCalculator инжектирован");
    }

    void Awake() => RecalcStats(rebuildVisuals: false);

    void Start()
    {
        _playerStats = GetComponentInParent<PlayerStats>();
        _playerNetwork = GetComponentInParent<NetworkBehaviour>();

        if (_playerStats != null)
        {
            _playerStats.RegisterShield(this);
            _playerStats.AddBonusMaxHealth(HEALTH_BONUS);
        }
        else
        {
            Debug.LogWarning("[AspisWeapon] PlayerStats не найден у родителя!");
        }

        RebuildShieldVisuals();
        Debug.Log($"[AspisWeapon] Init: charges={_currentCharges}/{_currentMaxCharges}, " +
                  $"regen={_currentRegenCooldown:F1}s, crit={_currentCritChance * 100f:F0}%×{_currentCritMultiplier:F1}");
    }

    void OnDestroy()
    {
        if (_playerStats != null)
        {
            _playerStats.UnregisterShield(this);
            _playerStats.RemoveBonusMaxHealth(HEALTH_BONUS);
        }
        DestroyShieldVisuals();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПЕРЕСЧЁТ ХАРАКТЕРИСТИК
    // ─────────────────────────────────────────────────────────────────────────

    void RecalcStats(bool rebuildVisuals = true)
    {
        int newMax = baseShieldCount + _bonusExtraCharges;
        bool chargesChanged = newMax != _currentMaxCharges;

        if (chargesChanged)
        {
            int added = newMax - _currentMaxCharges;
            _currentMaxCharges = newMax;
            if (added > 0)
                _currentCharges = Mathf.Min(_currentCharges + added, _currentMaxCharges);
            if (_currentCharges == 0 && _currentMaxCharges > 0)
                _currentCharges = _currentMaxCharges;
        }

        _currentRegenCooldown = baseRegenCooldown / (1f + _bonusRegenSpeedPct / 100f);
        // Overflow крит.шанса → бонус крит.урона (та же формула что в PlayerStatSheet)
        float rawCritChance = baseCritChance + _bonusCritChanceAbs;
        _currentCritChance = Mathf.Clamp01(rawCritChance);
        float critOverflow = Mathf.Max(0f, rawCritChance - 1f);
        float overflowMult = critOverflow > 0f ? Mathf.Sqrt(critOverflow) * 0.5f : 0f;
        _currentCritMultiplier = baseCritMultiplier + _bonusCritMultiplierAbs + overflowMult;

        if (rebuildVisuals && chargesChanged)
            RebuildShieldVisuals();
        else if (rebuildVisuals)
            UpdateShieldActiveState();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE — ОРБИТА + ВОССТАНОВЛЕНИЕ
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        _orbitAngle = (_orbitAngle + orbitSpeed * Time.deltaTime) % 360f;
        UpdateShieldPositions();

        if (!IsLocalPlayer()) return;
        TickRegenTimer();
    }

    private void TickRegenTimer()
    {
        if (_currentCharges >= _currentMaxCharges) return;

        _regenTimer -= Time.deltaTime;
        if (_regenTimer <= 0f)
        {
            _currentCharges = Mathf.Min(_currentCharges + 1, _currentMaxCharges);
            UpdateShieldActiveState();
            Debug.Log($"[AspisWeapon] ♻️ Заряд восстановлен! {_currentCharges}/{_currentMaxCharges}");

            if (_currentCharges < _currentMaxCharges)
                _regenTimer = _currentRegenCooldown;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IShieldInterceptor
    // ─────────────────────────────────────────────────────────────────────────

    public float InterceptDamage(float rawDamage, EnemyHealth attacker)
    {
        if (_currentCharges <= 0) return rawDamage;

        ConsumeCharge();

        float reducedDamage = rawDamage * (1f - DAMAGE_REDUCTION);

        if (attacker != null)
        {
            float retaliationBase = rawDamage * RETALIATION_FRACTION;
            float finalRetaliation = CalculateRetaliationDamage(retaliationBase);
            attacker.TakeDamage(finalRetaliation, _playerStats);
        }

        Debug.Log($"[AspisWeapon] 🛡️ Щит! {rawDamage:F1}→{reducedDamage:F1}, " +
                  $"заряды={_currentCharges}/{_currentMaxCharges}");
        return reducedDamage;
    }

    private void ConsumeCharge()
    {
        bool wasFull = (_currentCharges == _currentMaxCharges);
        _currentCharges = Mathf.Max(0, _currentCharges - 1);
        UpdateShieldActiveState();
        if (wasFull) _regenTimer = _currentRegenCooldown;
    }

    private float CalculateRetaliationDamage(float baseDmg)
    {
        if (_currentCritChance > 0f && Random.value < _currentCritChance)
        {
            float critDmg = baseDmg * _currentCritMultiplier;
            Debug.Log($"[AspisWeapon] ✨ КРИТ! {baseDmg:F1}→{critDmg:F1} (×{_currentCritMultiplier:F1})");
            return critDmg;
        }
        return baseDmg;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВИЗУАЛ
    // ─────────────────────────────────────────────────────────────────────────

    private void RebuildShieldVisuals()
    {
        DestroyShieldVisuals();

        for (int i = 0; i < _currentMaxCharges; i++)
        {
            GameObject seg;
            if (shieldSegmentPrefab != null)
            {
                seg = Instantiate(shieldSegmentPrefab, Vector3.zero, Quaternion.identity, null);
            }
            else
            {
                seg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                seg.transform.localScale = segmentScale;
                var col = seg.GetComponent<Collider>();
                if (col != null) Destroy(col);
                var rend = seg.GetComponent<Renderer>();
                if (rend != null) rend.material.color = new Color(0.2f, 0.5f, 1f, 0.85f);
            }

            seg.name = $"ShieldSegment_{i}";
            _shieldVisuals.Add(seg);
        }

        UpdateShieldActiveState();
        UpdateShieldPositions();
    }

    private void DestroyShieldVisuals()
    {
        foreach (var vis in _shieldVisuals)
            if (vis != null) Destroy(vis);
        _shieldVisuals.Clear();
    }

    private void UpdateShieldPositions()
    {
        int count = _shieldVisuals.Count;
        if (count == 0) return;

        Vector3 center = transform.parent != null ? transform.parent.position : transform.position;
        float step = 360f / count;

        for (int i = 0; i < count; i++)
        {
            if (_shieldVisuals[i] == null) continue;
            float rad = (_orbitAngle + step * i) * Mathf.Deg2Rad;
            _shieldVisuals[i].transform.position = new Vector3(
                center.x + Mathf.Cos(rad) * orbitRadius,
                center.y + 0.5f,
                center.z + Mathf.Sin(rad) * orbitRadius);
        }
    }

    private void UpdateShieldActiveState()
    {
        for (int i = 0; i < _shieldVisuals.Count; i++)
        {
            if (_shieldVisuals[i] != null)
                _shieldVisuals[i].SetActive(i < _currentCharges);
        }
    }

    private bool IsLocalPlayer()
    {
        if (GameModeManager.IsMode(GameMode.SinglePlayer)) return _playerStats != null;
        return _playerNetwork != null && _playerNetwork.IsOwner;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDynamicUpgradeable
    // ─────────────────────────────────────────────────────────────────────────

    public float? GetCurrentStatValue(StatBonusType stat)
    {
        switch (stat)
        {
            case StatBonusType.ShieldCharge: return _currentMaxCharges;
            case StatBonusType.ShieldRegen: return _currentRegenCooldown;
            case StatBonusType.CritChance: return _currentCritChance * 100f;
            case StatBonusType.CritMultiplier: return _currentCritMultiplier;
            default: return null;
        }
    }

    public StatBonusType[] GetAvailableStats() => new[]
    {
        StatBonusType.ShieldCharge,    // "+1 заряд(ов) щита"
        StatBonusType.ShieldRegen,     // "+X% скорость восстановления щита"
        StatBonusType.CritChance,
        StatBonusType.CritMultiplier,
    };

    public void ApplyDynamicUpgrade(WeaponUpgradeOffer offer)
    {
        Debug.Log($"⬆️ [AspisWeapon] {offer.Rarity} — ДО: " +
                  $"charges={_currentMaxCharges}, regen={_currentRegenCooldown:F2}s");

        foreach (var bonus in offer.Bonuses)
        {
            switch (bonus.type)
            {
                case StatBonusType.ShieldCharge:
                    _bonusExtraCharges += (int)bonus.value; break;
                case StatBonusType.ShieldRegen:
                    _bonusRegenSpeedPct += bonus.value; break;
                case StatBonusType.CritChance:
                    _bonusCritChanceAbs += bonus.value / 100f; break;
                case StatBonusType.CritMultiplier:
                    _bonusCritMultiplierAbs += bonus.value; break;
            }
        }

        RecalcStats(rebuildVisuals: true);

        Debug.Log($"⬆️ [AspisWeapon] Готово — charges={_currentMaxCharges}, regen={_currentRegenCooldown:F2}s");
    }

    // ─────────────────────────────────────────────────────────────────────────

    public int GetCurrentCharges() => _currentCharges;
    public int GetMaxCharges() => _currentMaxCharges;
    public float GetRegenCooldown() => _currentRegenCooldown;
    public float GetRegenProgress() => (_currentCharges < _currentMaxCharges && _currentRegenCooldown > 0f)
                                         ? 1f - (_regenTimer / _currentRegenCooldown) : 1f;
    public float GetCritChance() => _currentCritChance;
    public float GetCritMultiplier() => _currentCritMultiplier;

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.5f, 1f, 0.4f);
        Gizmos.DrawWireSphere(transform.position, orbitRadius);
    }
#endif
}
*/