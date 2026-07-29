using UnityEngine;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════
/// ACID DEBUFF COMPONENT — компонент кислотного дебаффа на враге
/// ══════════════════════════════════════════════════════════════════════
///
/// Добавляется на GameObject врага когда он входит в кислотную лужу.
/// Деактивируется когда враг покидает все лужи.
///
/// Хранит счётчик луж (_pudbleCount):
///   враг может стоять в нескольких лужах одновременно.
///   Дебафф активен пока _pudbleCount > 0.
///
/// ИСПРАВЛЕНИЕ: убраны Debug.Log из AddPuddle/RemovePuddle.
///   Эти методы вызываются при каждом Enter/Exit каждого врага в луже —
///   это горячий путь. При 700 врагах лог создавал тысячи строк/сек.
///   Логи перенесены за #if UNITY_EDITOR || DEVELOPMENT_BUILD.
///
/// ИСПОЛЬЗОВАНИЕ В CombatCalculator:
///   var debuff = target.GetComponent&lt;AcidDebuffComponent&gt;();
///   if (debuff != null && debuff.IsActive)
///       damage *= AcidDebuffComponent.DAMAGE_BONUS_MULTIPLIER;
/// </summary>
public class AcidDebuffComponent : MonoBehaviour
{
    /// <summary>
    /// Множитель входящего урона для дебаффнутого врага.
    /// +7% от всех источников урона пока враг в луже.
    /// </summary>
    public const float DAMAGE_BONUS_MULTIPLIER = 1.07f;

    // Сколько луж сейчас накрывают этого врага
    private int _pudbleCount = 0;

    /// <summary>true — враг сейчас в кислотной луже</summary>
    public bool IsActive => _pudbleCount > 0;

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Вызывается из AcidPuddle когда враг входит в зону лужи.
    /// </summary>
    public void AddPuddle()
    {
        _pudbleCount++;

        // ИСПРАВЛЕНИЕ: убран Debug.Log из горячего пути.
        // При 700 врагах это создавало тысячи строк лога в секунду.
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[AcidDebuff] {gameObject.name} +1 лужа (итого: {_pudbleCount})");
        #endif
    }

    /// <summary>
    /// Вызывается из AcidPuddle когда враг покидает зону или лужа истекает.
    /// </summary>
    public void RemovePuddle()
    {
        _pudbleCount = Mathf.Max(0, _pudbleCount - 1);

        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[AcidDebuff] {gameObject.name} -1 лужа (итого: {_pudbleCount})");
        #endif

        if (_pudbleCount == 0)
            enabled = false; // отключаем компонент вместо Destroy — безопаснее с пулами
    }

    /// <summary>
    /// Вызывается из EnemyHealth.ResetState() при возврате врага в пул.
    /// Полностью сбрасывает дебафф.
    /// </summary>
    public void ResetDebuff()
    {
        _pudbleCount = 0;
    }
}
