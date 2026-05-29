using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class RadialHillFollower : MonoBehaviour
{
    [Header("Handles")]
    public Transform handle;
    public List<Transform> additionalHandles = new List<Transform>();

    [Header("Control")]
    public float heightMultiplier = 1f;
    public float defaultRadius = 5f;

    [Header("Radius Source")]
    public bool useHandleScale = true;
    public float scaleMultiplier = 3f; // Multiplier pour convertir scale en radius
    public bool useHandleComponent = true;

    [Header("Offset")]
    public float yOffset = 0f;

    Vector3 _basePosition;
    bool _hasStoredBase = false;

    // Reusable list — avoids per-frame allocation
    private readonly List<Transform> _handlesScratch = new List<Transform>();

    void Start()
    {
        StoreBasePosition();
    }

    void Update()
    {
        Apply();
    }

    void StoreBasePosition()
    {
        if (!_hasStoredBase)
        {
            _basePosition = transform.position;
            _hasStoredBase = true;
        }
    }

    void Apply()
    {
        StoreBasePosition();

        // Collect all handles (reuse scratch list)
        _handlesScratch.Clear();
        if (handle != null) _handlesScratch.Add(handle);
        if (additionalHandles != null)
        {
            foreach (var h in additionalHandles)
                if (h != null) _handlesScratch.Add(h);
        }

        var handles = _handlesScratch;

        if (handles.Count == 0)
        {
            // No handles, return to base position
            Vector3 resetPos = _basePosition;
            resetPos.y += yOffset;
            transform.position = resetPos;
            return;
        }

        // Calculate deformation at object position
        float totalDeformation = 0f;
        Vector2 objectPos = new Vector2(_basePosition.x, _basePosition.z);

        foreach (var h in handles)
        {
            // Get radius for this specific handle
            float handleRadius = GetRadiusFromHandle(h);

            // Distance from handle in XZ plane
            Vector2 handlePos = new Vector2(h.position.x, h.position.z);
            float distance = Vector2.Distance(objectPos, handlePos);

            // Simple linear falloff
            float influence = Mathf.Clamp01(1f - distance / handleRadius);

            // Height contribution from this handle
            float handleHeight = h.position.y * heightMultiplier;
            totalDeformation += handleHeight * influence;
        }

        // Apply deformation
        Vector3 newPos = _basePosition;
        newPos.y += totalDeformation + yOffset;
        transform.position = newPos;
    }

    float GetRadiusFromHandle(Transform handle)
    {
        // Try to get radius from component first
        if (useHandleComponent)
        {
            var radiusComponent = handle.GetComponent<HandleRadius>();
            if (radiusComponent != null)
                return radiusComponent.radius;
        }

        // Fall back to scale-based radius
        if (useHandleScale)
        {
            // Use average of X and Z scale, multiplied by scaleMultiplier
            Vector3 scale = handle.lossyScale;
            float avgScale = (Mathf.Abs(scale.x) + Mathf.Abs(scale.z)) * 0.5f;
            return avgScale * scaleMultiplier;
        }

        // Default fallback
        return defaultRadius;
    }

    void OnDisable()
    {
        // Restore base position when disabled
        if (_hasStoredBase)
        {
            transform.position = _basePosition;
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Store Current Position as Base")]
    void StoreCurrentAsBase()
    {
        _basePosition = transform.position;
        _hasStoredBase = true;
        UnityEditor.EditorUtility.SetDirty(this);
    }

    [ContextMenu("Reset to Base Position")]
    void ResetToBase()
    {
        if (_hasStoredBase)
        {
            transform.position = _basePosition;
            UnityEditor.EditorUtility.SetDirty(gameObject);
        }
    }
#endif
}

// Simple component to define radius on handles
[System.Serializable]
public class HandleRadius : MonoBehaviour
{
    public float radius = 5f;
}