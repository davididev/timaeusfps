using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[DisallowMultipleComponent]
public class FaceEditorCamera : MonoBehaviour
{
#if UNITY_EDITOR
    [Tooltip("Ne pivote que si l'objet est sélectionné.")]
    public bool onlyWhenSelected = false;

    [Tooltip("Contraindre la rotation à l'axe Y (billboard plan).")]
    public bool yAxisOnly = true;

    [Tooltip("Cible alternative (par défaut: caméra de la dernière SceneView active).")]
    public Transform cameraOverride;

    private const float AngleThresholdDeg = 0.1f;

    private void OnEnable()
    {
        if (!Application.isPlaying)
            EditorApplication.update += FaceCamera;
    }

    private void OnDisable()
    {
        EditorApplication.update -= FaceCamera;
    }

    private void FaceCamera()
    {
        if (Application.isPlaying) return;
        if (this == null)
        {
            EditorApplication.update -= FaceCamera;
            return;
        }
        if (!isActiveAndEnabled) return;
        if (transform == null)
        {
            EditorApplication.update -= FaceCamera;
            return;
        }

        if (onlyWhenSelected)
        {
            // Si on limite au(s) objet(s) sélectionné(s)
            bool isSelected = false;
            foreach (var t in Selection.transforms)
            {
                if (t == transform) { isSelected = true; break; }
            }
            if (!isSelected) return;
        }

        // Choix de la caméra cible
        Transform camT = cameraOverride != null ? cameraOverride : GetSceneViewCamera();
        if (camT == null) return;

        Vector3 dir = camT.position - transform.position;
        if (yAxisOnly) dir.y = 0f;

        if (dir.sqrMagnitude < 1e-6f) return;

        Quaternion newRot = Quaternion.LookRotation(dir.normalized, Vector3.up);

        // Ne modifie la rotation que si elle change vraiment (évite de marquer la scène dirty en boucle)
        if (Quaternion.Angle(transform.rotation, newRot) > AngleThresholdDeg)
            transform.rotation = newRot;
    }

    private static Transform GetSceneViewCamera()
    {
        // Dernière SceneView active
        var sv = SceneView.lastActiveSceneView;
        if (sv != null && sv.camera != null) return sv.camera.transform;

        // Fallback: première caméra de SceneView disponible
        var cams = SceneView.GetAllSceneCameras();
        if (cams != null && cams.Length > 0) return cams[0].transform;

        return null;
    }
#endif
}
