using UnityEngine;

/// <summary>
/// ExperienceOrb — пассивный контейнер данных.
///
/// ОПТИМИЗАЦИЯ: Update() полностью убран из ExperienceOrb.
/// Движение всех орбов обрабатывается одним циклом в ExperienceOrbPool.Update().
/// 1000 орбов = 1 Update-диспатч вместо 1000.
/// </summary>
public class ExperienceOrb : MonoBehaviour
{
    [Header("Характеристики")]
    public int xpAmount = 10;
    public float speed = 15f;

    [Header("Визуал")]
    [Tooltip("true = дополнительный орб (другой цвет, от Академичной Шляпы)")]
    public bool IsBonus = false;

    [SerializeField] private Color normalColor = new Color(0.2f, 0.8f, 1f, 1f);
    [SerializeField] private Color bonusColor = new Color(1f, 0.8f, 0.1f, 1f);

    // ── Владелец орба (ClientId игрока-убийцы) ───────────────────────────────
    // ulong.MaxValue = без владельца (SinglePlayer / legacy)
    public ulong OwnerClientId { get; private set; } = ulong.MaxValue;

    // Читается ExperienceOrbPool каждый кадр
    public Transform Target { get; private set; }
    public bool IsAttracting { get; private set; }

    private Renderer _renderer;

    private void Awake()
    {
        _renderer = GetComponentInChildren<Renderer>();
    }

    public void StartAttract(Transform playerTransform)
    {
        Target = playerTransform;
        IsAttracting = true;
    }

    public void Setup(int xp, bool isBonus, ulong ownerClientId = ulong.MaxValue)
    {
        xpAmount = xp;
        IsBonus = isBonus;
        OwnerClientId = ownerClientId;
        ApplyColor();
    }

    public void ResetOrb()
    {
        Target = null;
        IsAttracting = false;
        OwnerClientId = ulong.MaxValue;
        ApplyColor();
    }

    public void GiveXP()
    {
        if (Target == null) return;
        var stats = Target.GetComponent<PlayerStats>();
        stats?.AddExperience(xpAmount);
    }

    private void ApplyColor()
    {
        if (_renderer == null) return;
        _renderer.material.color = IsBonus ? bonusColor : normalColor;
    }
}