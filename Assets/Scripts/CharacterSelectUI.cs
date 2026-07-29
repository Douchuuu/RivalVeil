using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using VContainer;

public class CharacterSelectUI : MonoBehaviour
{
    [Header("── Контейнер кнопок ────────────────────────────────────────────")]
    [SerializeField] private Transform characterButtonContainer;
    [SerializeField] private GameObject characterButtonPrefab;

    [Header("── Панель выбранного персонажа ────────────────────────────────")]
    [SerializeField] private Image selectedPortraitImage;
    [SerializeField] private TextMeshProUGUI selectedNameText;
    [SerializeField] private TextMeshProUGUI selectedDescriptionText;
    [SerializeField] private TextMeshProUGUI selectedPassiveText;
    [SerializeField] private GameObject selectedPanel;

    [Header("── Кнопка подтверждения ─────────────────────────────────────────")]
    [SerializeField] private Button confirmButton;
    [SerializeField] private TextMeshProUGUI confirmButtonText;

    [Header("── Отображение оппонента ─────────────────────────────────────────")]
    [SerializeField] private TextMeshProUGUI opponentSelectionText;
    [SerializeField] private Image opponentPortraitImage;

    [Header("── Имена дочерних объектов в prefab кнопки ─────────────────────")]
    [SerializeField] private string portraitChildName = "PortraitImage";
    [SerializeField] private string nameChildName = "NameText";
    [SerializeField] private string borderChildName = "BorderImage";

    [Header("── Настройки выделения ─────────────────────────────────────────")]
    [SerializeField] private Color selectedBorderColor = new Color(1f, 0.85f, 0f);
    [SerializeField] private Color unselectedBorderColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
    
    
    private CharacterDatabase _characterDb;
    private CharacterSelectManager _selectManager;
    private readonly List<GameObject> _characterButtons = new List<GameObject>();
    
    [Inject]
    public void Construct(CharacterDatabase characterDb)
    {
        _characterDb = characterDb;
    }
    private void Awake()
    {
        // 2. ПРИВЯЗКА КНОПКИ: Чтобы метод OnConfirmClicked работал
        if (confirmButton != null)
        {
            confirmButton.onClick.AddListener(OnConfirmClicked);
        }
    }
    private void Start()
    {
        if (GameModeManager.Instance != null)
        {
            InitializeUI();
        }
        else
        {
            Debug.Log("[CharacterSelectUI] Ожидание инициализации GameModeManager...");
            GameModeManager.OnInstanceInitialized += InitializeUI;
        }
    }

    private void InitializeUI()
    {
        // Отписываемся, чтобы не вызвать дважды
        GameModeManager.OnInstanceInitialized -= InitializeUI;

        // Теперь подписываемся на события
        GameModeManager.Instance.OnGameModeChanged += OnGameModeChanged;

        // И запускаем твою корутину ожидания CharacterSelectManager
        StartCoroutine(WaitForSelectManager());

        // Принудительно обновляем состояние
        OnGameModeChanged(GameModeManager.GetCurrentMode());
    }

    private void SubscribeToManager()
    {
        // Отписываемся от ожидания (на всякий случай), чтобы не вызвать дважды
        GameModeManager.OnInstanceInitialized -= SubscribeToManager;

        // Теперь мы на 100% уверены, что Instance существует
        GameModeManager.Instance.OnGameModeChanged += OnGameModeChanged;

        // Сразу проверяем текущее состояние
        OnGameModeChanged(GameModeManager.GetCurrentMode());
        StartCoroutine(WaitForSelectManager());
        Debug.Log("[CharacterSelectUI] Успешно подписан на GameModeManager");
    }

    private void OnDestroy()
    {
        // Важно отписаться, чтобы не было утечек памяти
        if (GameModeManager.Instance != null)
        {
            GameModeManager.Instance.OnGameModeChanged -= OnGameModeChanged;
        }
        GameModeManager.OnInstanceInitialized -= SubscribeToManager;
    }

    private IEnumerator WaitForSelectManager()
    {
        while (_selectManager == null)
        {
            var allManagers = FindObjectsByType<CharacterSelectManager>(FindObjectsSortMode.None);
            foreach (var mgr in allManagers)
            {
                if (mgr.IsOwner || GameModeManager.IsMode(GameMode.SinglePlayer))
                {
                    _selectManager = mgr;
                    break;
                }
            }
            yield return new WaitForSeconds(0.2f);
        }

        _selectManager.OnCharacterSelected += OnMyCharacterChanged;
        _selectManager.OnOpponentCharacterSelected += OnOpponentCharacterChanged;

        if (_selectManager.HasSelectedCharacter())
            SelectButton(_selectManager.GetSelectedIndex());
    }

    private void BuildCharacterButtons()
    {
        // Убираем тихий return и заменяем на громкие ошибки
        if (characterButtonContainer == null)
        {
            Debug.LogError("[CharacterSelectUI] ❌ ОШИБКА: Не назначен characterButtonContainer в инспекторе!");
            return;
        }

        if (_characterDb == null)
        {
            // Fallback через Resources
            _characterDb = Resources.Load<CharacterDatabase>("CharacterDatabase");
            if (_characterDb == null)
            {
                Debug.LogError("[CharacterSelectUI] ❌ ОШИБКА: _characterDb равен NULL! VContainer не внедрил зависимость. Убедись что CharacterDatabase назначена в BaseLifetimeScope Inspector.");
                return;
            }
            Debug.LogWarning("[CharacterSelectUI] CharacterDatabase загружена из Resources как fallback.");
        }

        // Чистим старое
        foreach (Transform child in characterButtonContainer) Destroy(child.gameObject);
        _characterButtons.Clear();

        var all = _characterDb.GetAll();
        for (int i = 0; i < all.Count; i++)
        {
            int capturedIndex = i;
            var def = all[i];

            GameObject btnGO = characterButtonPrefab != null
                ? Instantiate(characterButtonPrefab, characterButtonContainer)
                : CreateFallbackButton(def);

            RectTransform rt = btnGO.GetComponent<RectTransform>();
            if (rt != null) rt.localScale = Vector3.one;

            FillButton(btnGO, def);

            var button = btnGO.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => OnCharacterButtonClicked(capturedIndex));
            }

            _characterButtons.Add(btnGO);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(characterButtonContainer.GetComponent<RectTransform>());
    }

    private void FillButton(GameObject btnGO, CharacterDefinition def)
    {
        var portrait = FindChild<Image>(btnGO, portraitChildName);
        if (portrait != null) portrait.sprite = def.portrait;

        var nameText = FindChild<TextMeshProUGUI>(btnGO, nameChildName);
        if (nameText != null) nameText.text = def.displayName;

        var border = FindChild<Image>(btnGO, borderChildName);
        if (border != null) border.color = unselectedBorderColor;
    }

    private void OnCharacterButtonClicked(int index)
    {
        if (_selectManager != null) _selectManager.SelectCharacter(index);
        SelectButton(index);
    }

    private void SelectButton(int index)
    {
        if (index < 0 || index >= _characterButtons.Count) return;

        for (int i = 0; i < _characterButtons.Count; i++)
        {
            var border = FindChild<Image>(_characterButtons[i], borderChildName);
            if (border != null)
                border.color = (i == index) ? selectedBorderColor : unselectedBorderColor;
        }

        var def = _characterDb.GetByIndex(index);
        if (def != null) ShowCharacterInfo(def);

        if (confirmButton != null) confirmButton.interactable = true;
    }

    private void ShowCharacterInfo(CharacterDefinition def)
    {
        if (selectedPanel != null) selectedPanel.SetActive(true);
        if (selectedPortraitImage != null) selectedPortraitImage.sprite = def.portrait;
        if (selectedNameText != null) selectedNameText.text = def.displayName;
        if (selectedDescriptionText != null) selectedDescriptionText.text = def.description;

        if (selectedPassiveText != null)
        {
            selectedPassiveText.text = def.passiveType == CharacterPassiveType.None
                ? "Нет пассивки"
                : $"[ПАССИВКА] {def.passiveType} (+{def.passiveValue:F0})";
        }
    }

    private void OnMyCharacterChanged(CharacterDefinition def)
    {
        int index = _characterDb.GetIndex(def);
        SelectButton(index);
    }

    private void OnOpponentCharacterChanged(CharacterDefinition def)
    {
        if (opponentSelectionText != null) opponentSelectionText.text = $"Оппонент: {def.displayName}";
        if (opponentPortraitImage != null) opponentPortraitImage.sprite = def.portrait;
    }

    private void OnConfirmClicked()
    {
        if (confirmButton != null) confirmButton.interactable = false;
        if (confirmButtonText != null) confirmButtonText.text = "Ожидание...";

        StartCoroutine(ConfirmWithRetry());
    }

    private System.Collections.IEnumerator ConfirmWithRetry()
    {
        // Ждём пока GameModeManager будет готов (максимум 10 сек)
        float waited = 0f;
        while (GameModeManager.Instance == null && waited < 10f)
        {
            waited += 0.1f;
            yield return new WaitForSeconds(0.1f);
        }

        if (GameModeManager.Instance == null)
        {
            Debug.LogError("[CharacterSelectUI] GameModeManager не найден! Проверь GameModeSpawner на сцене.");
            if (confirmButton != null) confirmButton.interactable = true;
            if (confirmButtonText != null) confirmButtonText.text = "Ошибка — попробуй снова";
            yield break;
        }

        if (GameStartConfig.IsSinglePlayer)
            GameModeManager.Instance.SetGameModeServerRpc((int)GameMode.SinglePlayer);
        else
            GameModeManager.Instance.SetPlayerReadyServerRpc();
    }

    private void OnGameModeChanged(GameMode newMode)
    {
        bool shouldShow = (newMode == GameMode.Selecting);
        gameObject.SetActive(shouldShow);
        if (shouldShow) BuildCharacterButtons();
    }

    private T FindChild<T>(GameObject parent, string childName) where T : Component
    {
        if (parent == null) return null;
        Transform t = parent.transform.Find(childName);
        if (t != null) return t.GetComponent<T>();

        // Глубокий поиск если не нашли на первом уровне
        foreach (var comp in parent.GetComponentsInChildren<T>(true))
            if (comp.gameObject.name == childName) return comp;

        return null;
    }

    private GameObject CreateFallbackButton(CharacterDefinition def)
    {
        GameObject go = new GameObject($"Btn_{def.id}", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(characterButtonContainer, false);
        return go;
    }

    public void Show() => gameObject.SetActive(true);
    public void Hide() => gameObject.SetActive(false);
}