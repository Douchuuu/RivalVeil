using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using VContainer;

/// <summary>
/// LevelUpManager v5 — уведомляет WeaponManager об апгрейдах оружий.
///
/// ══════════════════════════════════════════════════════════════
/// ИЗМЕНЕНИЕ:
/// ══════════════════════════════════════════════════════════════
///
///   В ApplyOffer() после применения WeaponUpgrade добавлен вызов:
///     _currentWeaponManager.NotifyWeaponUpgraded(targetWeaponId)
///
///   Это увеличивает уровень оружия на 1 в WeaponManager.
///   UpgradeGenerator читает уровень и исключает максовые оружия.
///   BasePlayerUIManager читает уровень и отображает цифру на иконке.
///
///   Для тотемов уровень увеличивается внутри TotemManager.UpgradeTotem()
///   и UnlockTotem() — дополнительных вызовов не нужно.
/// ══════════════════════════════════════════════════════════════
/// </summary>
public class LevelUpManager : MonoBehaviour
{
    [System.Obsolete("Используй GameStateService.IsPaused вместо LevelUpManager.IsPaused")]
    public static bool IsPaused => _gameStateStatic?.IsPaused ?? false;
    private static GameStateService _gameStateStatic;

    // ─────────────────────────────────────────────────────────────────────────

    [Header("SinglePlayer")]
    [SerializeField] private GameObject singlePlayerLevelUpPanel;
    [SerializeField] private Canvas singlePlayerCanvas;

    [Header("Competitive")]
    [SerializeField] private GameObject competitiveLevelUpPanel;
    [SerializeField] private Canvas competitiveCanvas;

    [Header("Кол-во карточек")]
    [SerializeField] private int offerCount = 3;

    [Header("Балансировка апгрейдов")]
    [SerializeField] private UpgradeBalanceConfig upgradeBalanceConfig;

    // ─────────────────────────────────────────────────────────────────────────

    private GameObject _activePanel;
    private Canvas _activeCanvas;
    private WeaponManager _currentWeaponManager;
    private TotemManager _currentTotemManager;
    private int _currentPlayerLevel = 1;

    // ─── INJECTION ────────────────────────────────────────────────────────────

    private WeaponDatabase _weaponDb;
    private TotemDatabase _totemDb;
    private LocalGameTimer _localTimer;
    private GameStateService _gameState;

    [Inject]
    public void Construct(
        WeaponDatabase weaponDb,
        TotemDatabase totemDb,
        LocalGameTimer localTimer,
        GameStateService gameState)
    {
        _weaponDb = weaponDb;
        _totemDb = totemDb;
        _localTimer = localTimer;
        _gameState = gameState;
        _gameStateStatic = gameState;
    }

    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        singlePlayerLevelUpPanel?.SetActive(false);
        competitiveLevelUpPanel?.SetActive(false);
        FindCanvasesAcrossScenes();
        ValidateReferences();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CROSS-SCENE ПОИСК CANVAS
    // ─────────────────────────────────────────────────────────────────────────

    private void FindCanvasesAcrossScenes()
    {
        const string SP_CANVAS = "SinglePlayerUI_Canvas";
        const string COMP_CANVAS = "CompetitiveUIManager_Canvas";

        bool needSP = singlePlayerCanvas == null;
        bool needComp = competitiveCanvas == null;
        bool needSPPanel = singlePlayerLevelUpPanel == null;
        bool needCompPanel = competitiveLevelUpPanel == null;

        if (!needSP && !needComp && !needSPPanel && !needCompPanel) return;

        var allCanvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var c in allCanvases)
        {
            if (needSP && c.gameObject.name == SP_CANVAS) { singlePlayerCanvas = c; needSP = false; }
            if (needComp && c.gameObject.name == COMP_CANVAS) { competitiveCanvas = c; needComp = false; }
        }

        if (needSP || needComp)
        {
            foreach (var c in allCanvases)
            {
                string n = c.gameObject.name;
                if (needSP && n.Contains("SinglePlayer") && !n.Contains("LevelUp")) { singlePlayerCanvas = c; needSP = false; }
                if (needComp && n.Contains("Competitive") && !n.Contains("LevelUp")) { competitiveCanvas = c; needComp = false; }
            }
        }

        if (needSPPanel || needCompPanel)
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (needSPPanel) { var p = FindPanelByName(root, "SinglePlayer", "LevelUp"); if (p != null) { singlePlayerLevelUpPanel = p; needSPPanel = false; } }
                    if (needCompPanel) { var p = FindPanelByName(root, "Competitive", "LevelUp"); if (p != null) { competitiveLevelUpPanel = p; needCompPanel = false; } }
                }
            }
        }
    }

    private GameObject FindPanelByName(GameObject root, string a, string b)
    {
        if (root.name.Contains(a) && root.name.Contains(b)) return root;
        for (int i = 0; i < root.transform.childCount; i++)
        {
            var found = FindPanelByName(root.transform.GetChild(i).gameObject, a, b);
            if (found != null) return found;
        }
        return null;
    }

    private void ValidateReferences()
    {
        if (singlePlayerLevelUpPanel == null && competitiveLevelUpPanel == null)
            Debug.LogError("[LevelUpManager] ❌ Ни одна LevelUp-панель не назначена!");
        if (_weaponDb == null)
            Debug.LogError("[LevelUpManager] ❌ WeaponDatabase не инжектирован!");
        if (_totemDb == null)
            Debug.LogWarning("[LevelUpManager] ⚠️ TotemDatabase не инжектирован — тотемы не выпадают.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОТКРЫТИЕ МЕНЮ
    // ─────────────────────────────────────────────────────────────────────────

    public void ShowLevelUpMenu(WeaponManager weaponManager)
    {
        _currentWeaponManager = weaponManager;
        _currentTotemManager = weaponManager.GetComponent<TotemManager>();

        _activePanel = GameModeManager.IsMode(GameMode.SinglePlayer)
            ? singlePlayerLevelUpPanel : competitiveLevelUpPanel;
        _activeCanvas = GameModeManager.IsMode(GameMode.SinglePlayer)
            ? singlePlayerCanvas : competitiveCanvas;

        if (_activePanel == null)
        {
            Debug.LogError("[LevelUpManager] Активная панель не назначена!");
            return;
        }

        singlePlayerCanvas?.gameObject.SetActive(false);
        competitiveCanvas?.gameObject.SetActive(false);
        _activeCanvas?.gameObject.SetActive(true);

        _gameState?.Pause(PauseReason.LevelUp);
        if (!GameModeManager.IsCompetitiveMode()) Time.timeScale = 0f;

        _activePanel.SetActive(true);
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        var stats = weaponManager.GetComponent<PlayerStats>();
        _currentPlayerLevel = stats != null ? stats.CurrentLevel : 1;

        GenerateCards();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ГЕНЕРАЦИЯ КАРТОЧЕК
    // ─────────────────────────────────────────────────────────────────────────

    void GenerateCards()
    {
        Button[] buttons = _activePanel.GetComponentsInChildren<Button>();
        if (buttons == null || buttons.Length == 0)
        {
            Debug.LogError("[LevelUpManager] Кнопки не найдены в LevelUpPanel!");
            return;
        }

        IReadOnlyList<WeaponDefinition> allWeapons = _weaponDb?.GetAll() ?? new List<WeaponDefinition>();
        IReadOnlyList<TotemDefinition> allTotems = _totemDb?.GetAll();

        if (allWeapons.Count == 0) { Debug.LogError("[LevelUpManager] WeaponDatabase пуст!"); return; }

        var rarityRng  = CreateRarityRng();
        var personalRng = new System.Random(UnityEngine.Random.Range(0, int.MaxValue));

        int count = Mathf.Min(offerCount, buttons.Length);
        var offers = UpgradeGenerator.GenerateOffers(
            rarityRng, personalRng,
            allWeapons, _currentWeaponManager,
            count, upgradeBalanceConfig,
            allTotems, _currentTotemManager);

        for (int i = 0; i < count; i++)
        {
            if (i >= offers.Count) break;

            var offer = offers[i];
            var label = buttons[i].GetComponentInChildren<TextMeshProUGUI>();

            if (label != null)
            {
                GameObject weaponObj = null;
                if (offer.Type == LevelUpOfferType.WeaponUpgrade && _currentWeaponManager != null)
                    foreach (var w in _currentWeaponManager.activeWeapons)
                        if (w != null && w.name == offer.WeaponUpgrade.TargetWeaponId)
                        { weaponObj = w; break; }

                float totemAccumulated = 0f;
                if (offer.Type == LevelUpOfferType.TotemUpgrade && _currentTotemManager != null)
                    totemAccumulated = _currentTotemManager.GetAccumulatedValue(offer.TotemUpgrade.TotemId);

                label.text = offer.GetButtonText(weaponObj, totemAccumulated);
            }
            else
                Debug.LogWarning($"[LevelUpManager] Кнопка {buttons[i].name} не имеет TextMeshProUGUI!");

            buttons[i].onClick.RemoveAllListeners();
            var captured = offer;
            buttons[i].onClick.AddListener(() => ApplyOffer(captured));
            ApplyRarityColor(buttons[i], captured);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПРИМЕНЕНИЕ ВЫБОРА
    // ─────────────────────────────────────────────────────────────────────────

    void ApplyOffer(LevelUpOffer offer)
    {
        switch (offer.Type)
        {
            case LevelUpOfferType.NewWeapon:
                _currentWeaponManager?.UnlockWeapon(offer.WeaponDef);
                Debug.Log($"[LevelUpManager] ⚔️ Новое оружие: {offer.WeaponDef.displayName}");
                // UnlockWeapon уже устанавливает уровень = 1 внутри WeaponManager
                break;

            case LevelUpOfferType.WeaponUpgrade:
                if (_currentWeaponManager == null) break;

                bool applied = false;
                foreach (var w in _currentWeaponManager.activeWeapons)
                {
                    if (w == null || w.name != offer.WeaponUpgrade.TargetWeaponId) continue;
                    offer.WeaponUpgrade.Apply(w);
                    applied = true;

                    // ИЗМЕНЕНИЕ: уведомляем WeaponManager → уровень +1
                    _currentWeaponManager.NotifyWeaponUpgraded(offer.WeaponUpgrade.TargetWeaponId);

                    int newLevel = _currentWeaponManager.GetWeaponLevel(offer.WeaponUpgrade.TargetWeaponId);
                    Debug.Log($"[LevelUpManager] ⚔️ Апгрейд {offer.WeaponUpgrade.TargetWeaponName} " +
                              $"({offer.WeaponUpgrade.Rarity}) → Lv{newLevel}");
                    break;
                }

                if (!applied)
                    Debug.LogWarning($"[LevelUpManager] Оружие {offer.WeaponUpgrade.TargetWeaponId} не найдено!");
                break;

            case LevelUpOfferType.NewTotem:
                if (_currentTotemManager == null)
                { Debug.LogWarning("[LevelUpManager] TotemManager не найден!"); break; }
                // UnlockTotem устанавливает UpgradeLevel = 1 внутри TotemManager
                _currentTotemManager.UnlockTotem(offer.TotemDef, offer.TotemUpgrade.BonusValue);
                Debug.Log($"[LevelUpManager] 🗿 Новый тотем: {offer.TotemDef.displayName} " +
                          $"({offer.TotemUpgrade.Rarity}, +{offer.TotemUpgrade.BonusValue:F1}%) → Lv1");
                break;

            case LevelUpOfferType.TotemUpgrade:
                if (_currentTotemManager == null)
                { Debug.LogWarning("[LevelUpManager] TotemManager не найден!"); break; }
                // UpgradeTotem увеличивает UpgradeLevel +1 внутри TotemManager
                _currentTotemManager.UpgradeTotem(offer.TotemUpgrade.TotemId, offer.TotemUpgrade.BonusValue);
                int totemNewLevel = _currentTotemManager.GetTotemLevel(offer.TotemUpgrade.TotemId);
                Debug.Log($"[LevelUpManager] 🗿 Апгрейд тотема: {offer.TotemUpgrade.TotemName} " +
                          $"({offer.TotemUpgrade.Rarity}, +{offer.TotemUpgrade.BonusValue:F1}%) → Lv{totemNewLevel}");
                break;
        }

        CloseMenu();
    }

    // ─────────────────────────────────────────────────────────────────────────

    System.Random CreateRarityRng()
    {
        if (GameModeManager.IsCompetitiveMode() && GameModeManager.SharedSeed != 0)
        {
            int levelHash = _currentPlayerLevel * unchecked((int)2654435769);
            int seed = GameModeManager.SharedSeed ^ levelHash;
            return new System.Random(seed);
        }
        return new System.Random(UnityEngine.Random.Range(0, int.MaxValue));
    }

    void ApplyRarityColor(Button btn, LevelUpOffer offer)
    {
        var img = btn.GetComponent<Image>();
        if (img == null) return;
        if (offer.Type == LevelUpOfferType.NewWeapon) return;

        UpgradeRarity rarity = offer.Type switch
        {
            LevelUpOfferType.WeaponUpgrade => offer.WeaponUpgrade.Rarity,
            LevelUpOfferType.NewTotem      => offer.TotemUpgrade.Rarity,
            LevelUpOfferType.TotemUpgrade  => offer.TotemUpgrade.Rarity,
            _                              => UpgradeRarity.Common
        };

        Color c = rarity switch
        {
            UpgradeRarity.Common    => new Color(0.67f, 0.67f, 0.67f),
            UpgradeRarity.Rare      => new Color(0.27f, 0.60f, 1.00f),
            UpgradeRarity.Mythic    => new Color(0.67f, 0.27f, 1.00f),
            UpgradeRarity.Legendary => new Color(1.00f, 0.72f, 0.00f),
            _                       => Color.white
        };
        c.a = 0.25f;
        img.color = c;
    }

    void CloseMenu()
    {
        _activePanel?.SetActive(false);
        _gameState?.Resume();
        if (!GameModeManager.IsCompetitiveMode()) Time.timeScale = 1f;
        singlePlayerCanvas?.gameObject.SetActive(GameModeManager.IsMode(GameMode.SinglePlayer));
        competitiveCanvas?.gameObject.SetActive(GameModeManager.IsCompetitiveMode());
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
    }

    public bool IsMenuOpen => _activePanel != null && _activePanel.activeSelf;

    /// <summary>Алиас IsMenuOpen для совместимости с новой CursorManager.cs.</summary>
    public bool IsShowing => IsMenuOpen;
}
