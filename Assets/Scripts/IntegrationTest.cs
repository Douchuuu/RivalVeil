using UnityEngine;
using System.Collections;
using Unity.Netcode;

/// <summary>
/// Интеграционный тест.
/// ИСПРАВЛЕНО v3:
///   - charDef.characterId      → charDef.id
///   - charDef.damageMultiplier → charDef.damage
///   - charDef.moveSpeedMultiplier → charDef.moveSpeed
///   - charDef.maxHealth — float в новой CharacterDefinition
///   - GetSelectedCharacter() вместо .SelectedCharacter
///   - StatSheet.GetStat(StatType.X) вместо .Value
/// </summary>
public class IntegrationTest : MonoBehaviour
{
    [SerializeField] private bool  _runOnStart = true;
    [SerializeField] private float _testDelay  = 2f;
    [SerializeField] private string _testCharacterId = "ninja";

    private int _pass = 0, _fail = 0;

    void Start() { if (_runOnStart) StartCoroutine(RunAllTests()); }

    [ContextMenu("Run All Tests")]
    public void RunTests() => StartCoroutine(RunAllTests());

    private IEnumerator RunAllTests()
    {
        Debug.Log("[IT] === ИНТЕГРАЦИОННЫЕ ТЕСТЫ ===");
        yield return new WaitForSeconds(_testDelay);

        yield return T1_PlayerExists();
        yield return T2_Components();
        yield return T3_StatSheet();
        yield return T4_CharacterSelection();
        yield return T5_CharacterStats();
        yield return T6_WeaponInit();
        yield return T7_LevelUpManager();
        yield return T8_GameModeManager();

        Debug.Log($"[IT] ✅ {_pass}  ❌ {_fail}");
        if (_fail == 0) Debug.Log("[IT] 🎉 ВСЕ ПРОЙДЕНЫ!");
        else            Debug.LogError("[IT] ⚠️ ЕСТЬ ОШИБКИ!");
    }

    IEnumerator T1_PlayerExists()
    {
        var p = FindFirstObjectByType<PlayerStats>();
        if (p == null) Fail("PlayerStats не найден"); else Pass("PlayerStats найден");
        yield return null;
    }

    IEnumerator T2_Components()
    {
        var p = FindFirstObjectByType<PlayerStats>();
        if (p == null) { Fail("Нет игрока"); yield break; }
        bool ok = true;
        if (p.GetComponent<CharacterMultipliers>()   == null) { Debug.LogError("[IT] ❌ CharacterMultipliers"); ok = false; }
        if (p.GetComponent<CharacterSelectManager>() == null) { Debug.LogError("[IT] ❌ CharacterSelectManager"); ok = false; }
        if (p.GetComponent<WeaponManager>()          == null) { Debug.LogError("[IT] ❌ WeaponManager"); ok = false; }
        if (p.GetComponent<NetworkObject>()          == null) { Debug.LogError("[IT] ❌ NetworkObject"); ok = false; }
        if (ok) Pass("Все компоненты найдены"); else Fail("Компоненты отсутствуют");
        yield return null;
    }

    IEnumerator T3_StatSheet()
    {
        var p = FindFirstObjectByType<PlayerStats>();
        if (p?.StatSheet == null) { Fail("StatSheet NULL"); yield break; }
        Pass("StatSheet OK");
        Debug.Log($"[IT]   HP={p.StatSheet.GetStat(StatType.MaxHealth):F1}  DMG={p.StatSheet.GetStat(StatType.DamageMultiplier):F2}");
        yield return null;
    }

    IEnumerator T4_CharacterSelection()
    {
        var p = FindFirstObjectByType<PlayerStats>();
        if (p == null) { Fail("Нет игрока"); yield break; }
        var cs = p.GetComponent<CharacterSelectManager>();
        if (cs == null) { Fail("CharacterSelectManager нет"); yield break; }

        var def = cs.GetSelectedCharacter();
        if (def == null) { Fail("Персонаж не выбран"); yield break; }
        Pass($"Персонаж: {def.displayName}");
        // ИСПРАВЛЕНО: def.id вместо def.characterId
        Debug.Log($"[IT]   id={def.id}  weapon={def.startingWeaponId}");
        yield return null;
    }

    IEnumerator T5_CharacterStats()
    {
        var p = FindFirstObjectByType<PlayerStats>();
        if (p?.StatSheet == null) { Fail("StatSheet NULL"); yield break; }
        var def = p.GetComponent<CharacterSelectManager>()?.GetSelectedCharacter();
        if (def == null) { Fail("Нет персонажа"); yield break; }

        bool ok = true;

        // maxHealth — float в новой CharacterDefinition
        if (def.maxHealth > 0f)
        {
            float actual = p.StatSheet.GetStat(StatType.MaxHealth);
            if (Mathf.Abs(def.maxHealth - actual) > 5f)
            { Debug.LogError($"[IT] ❌ HP ожидалось {def.maxHealth:F0}, получено {actual:F0}"); ok = false; }
        }

        // ИСПРАВЛЕНО: def.damage (не damageMultiplier)
        if (Mathf.Abs(def.damage - 1f) > 0.01f)
        {
            float actual = p.StatSheet.GetStat(StatType.DamageMultiplier);
            if (Mathf.Abs(def.damage - actual) > 0.1f)
            { Debug.LogError($"[IT] ❌ DMG ожидалось {def.damage:F2}, получено {actual:F2}"); ok = false; }
        }

        // ИСПРАВЛЕНО: def.moveSpeed (не moveSpeedMultiplier)
        if (Mathf.Abs(def.moveSpeed - 1f) > 0.01f)
        {
            float actual = p.StatSheet.GetStat(StatType.MoveSpeedMultiplier);
            if (Mathf.Abs(def.moveSpeed - actual) > 0.1f)
            { Debug.LogError($"[IT] ❌ SPD ожидалось {def.moveSpeed:F2}, получено {actual:F2}"); ok = false; }
        }

        if (ok) Pass("Статы персонажа корректны"); else Fail("Несоответствие статов");
        yield return null;
    }

    IEnumerator T6_WeaponInit()
    {
        var p = FindFirstObjectByType<PlayerStats>();
        if (p == null) { Fail("Нет игрока"); yield break; }
        var wm = p.GetComponent<WeaponManager>();
        if (wm == null) { Fail("WeaponManager нет"); yield break; }

        if (string.IsNullOrEmpty(wm.PendingStartWeaponId)) Fail("PendingStartWeaponId пуст");
        else                                               Pass($"PendingWeapon: {wm.PendingStartWeaponId}");

        // HasInitializedWeapons — публичное свойство (добавлено в WeaponManager)
        if (!wm.HasInitializedWeapons) Fail("Оружие не инициализировано");
        else                           Pass("Оружие инициализировано");
        yield return null;
    }

    IEnumerator T7_LevelUpManager()
    {
        var lum = FindFirstObjectByType<LevelUpManager>();
        if (lum == null) { Fail("LevelUpManager нет"); yield break; }
        Pass("LevelUpManager найден");
        yield return null;
    }

    IEnumerator T8_GameModeManager()
    {
        if (GameModeManager.Instance == null) { Fail("GameModeManager нет"); yield break; }
        Pass("GameModeManager найден");
        if (FindFirstObjectByType<GameModeSpawner>() == null) Fail("GameModeSpawner нет");
        else                                                   Pass("GameModeSpawner найден");
        yield return null;
    }

    void Pass(string m) { _pass++; Debug.Log($"[IT] ✅ {m}"); }
    void Fail(string m) { _fail++; Debug.LogError($"[IT] ❌ {m}"); }
}
