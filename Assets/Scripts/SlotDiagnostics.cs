using UnityEngine;
using UnityEngine.UI;

public class SlotDiagnostics : MonoBehaviour
{
    void Start()
    {
        Debug.Log("=== WEAPON SLOTS DIAGNOSTICS ===");

        // 1. Проверяем есть ли контейнер с тегом
        GameObject container = GameObject.FindWithTag("UIInventory");
        if (container != null)
        {
            Debug.Log($"OK Container found: {container.name}");
            Debug.Log($"Path: {GetHierarchyPath(container)}");
        }
        else
        {
            Debug.LogError("ERROR: UIInventory container not found!");
            return;
        }

        // 2. Проверяем детей контейнера
        Debug.Log($"\nContainer children ({container.transform.childCount}):");
        int imageCount = 0;
        int slotCount = 0;

        foreach (Transform child in container.transform)
        {
            Image img = child.GetComponent<Image>();
            if (img != null)
            {
                imageCount++;
                slotCount++;
                Debug.Log($"  OK {child.name} - HAS Image component");
            }
            else
            {
                Debug.LogWarning($"  ERROR {child.name} - NO Image component!");
            }
        }

        Debug.Log($"\nTotal direct children with Image: {imageCount}");

        // 3. Проверяем есть ли WeaponManager
        WeaponManager wm = FindFirstObjectByType<WeaponManager>();
        if (wm != null)
        {
            Debug.Log($"\nOK WeaponManager found");
            Debug.Log($"Is owner: {wm.IsOwner}");
            Debug.Log($"Active weapons: {wm.activeWeapons.Count}");
        }
        else
        {
            Debug.LogError("ERROR: WeaponManager not found!");
        }

        Debug.Log("\n=== END DIAGNOSTICS ===");
    }

    string GetHierarchyPath(GameObject go)
    {
        string path = go.name;
        Transform current = go.transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }
        return path;
    }
}