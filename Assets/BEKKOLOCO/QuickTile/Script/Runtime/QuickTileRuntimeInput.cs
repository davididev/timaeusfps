// QuickTileRuntimeInput.cs
// Raycasts the mouse against the ground and drives QuickTileRuntimePainter.
// Uses the legacy Input API for MVP — swap to the new Input System later if needed.
// Hit detection order: physics raycast (layer mask) → XZ plane at editor.transform.y.

using UnityEngine;

namespace Bekkoloco
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(QuickTileRuntimePainter))]
    public class QuickTileRuntimeInput : MonoBehaviour
    {
        [Header("Refs")]
        public Camera targetCamera;
        public QuickTileRuntimePainter painter;

        [Header("Raycast")]
        public bool usePhysicsRaycast = true;
        public LayerMask groundMask = ~0;
        [Tooltip("Plane Y used when no physics hit is found. Defaults to editor transform Y.")]
        public float fallbackPlaneY;
        public bool autoFallbackY = true;

        [Header("Input")]
        public int paintButton = 0;   // left mouse
        public int eraseButton = 1;   // right mouse — swaps to Erase* mode while held
        public KeyCode cancelKey = KeyCode.Escape;

        [Header("Preview")]
        public bool showPreview = true;
        public Color previewColor = new Color(0f, 1f, 0.5f, 0.35f);

        GameObject previewGO;
        Renderer previewRenderer;
        Vector3Int currentCell;
        bool hasCell;
        QuickTileRuntimePainter.PaintMode modeBeforeErase;
        bool erasing;

        void Reset()
        {
            painter = GetComponent<QuickTileRuntimePainter>();
            targetCamera = Camera.main;
        }

        void Awake()
        {
            if (painter == null) painter = GetComponent<QuickTileRuntimePainter>();
            if (targetCamera == null) targetCamera = Camera.main;
        }

        void OnDisable()
        {
            if (painter != null && painter.StrokeActive) painter.EndStroke();
            if (previewGO != null) previewGO.SetActive(false);
        }

        void Update()
        {
            if (painter == null || !painter.IsReady || targetCamera == null) return;

            if (Input.GetKeyDown(cancelKey) && painter.StrokeActive)
                painter.EndStroke();

            hasCell = TryResolveCellUnderMouse(out currentCell);
            UpdatePreview();

            if (hasCell)
            {
                if (Input.GetMouseButtonDown(eraseButton)) EnterEraseMode();
                if (Input.GetMouseButtonUp(eraseButton))   ExitEraseMode();

                if (Input.GetMouseButtonDown(paintButton) || Input.GetMouseButtonDown(eraseButton))
                    painter.BeginStroke();

                if (Input.GetMouseButton(paintButton) || Input.GetMouseButton(eraseButton))
                    painter.PaintAt(currentCell);

                if (Input.GetMouseButtonUp(paintButton) || Input.GetMouseButtonUp(eraseButton))
                    painter.EndStroke();
            }
            else if (painter.StrokeActive
                     && !Input.GetMouseButton(paintButton)
                     && !Input.GetMouseButton(eraseButton))
            {
                painter.EndStroke();
            }
        }

        bool TryResolveCellUnderMouse(out Vector3Int cell)
        {
            cell = default;
            Vector3 mouse = Input.mousePosition;
            if (!IsPointerInScreen(mouse)) return false;

            Ray ray = targetCamera.ScreenPointToRay(mouse);
            Vector3 hitWorld;

            if (usePhysicsRaycast
                && Physics.Raycast(ray, out RaycastHit hit, 10000f, groundMask, QueryTriggerInteraction.Ignore))
            {
                hitWorld = hit.point;
            }
            else
            {
                float planeY = autoFallbackY && painter.editor != null
                    ? painter.editor.transform.position.y
                    : fallbackPlaneY;
                var plane = new Plane(Vector3.up, new Vector3(0f, planeY, 0f));
                if (!plane.Raycast(ray, out float dist)) return false;
                hitWorld = ray.GetPoint(dist);
            }

            cell = painter.editor.SafeWorldToCell(hitWorld);
            return true;
        }

        static bool IsPointerInScreen(Vector3 p)
            => p.x >= 0 && p.y >= 0 && p.x < Screen.width && p.y < Screen.height;

        void EnterEraseMode()
        {
            if (erasing) return;
            modeBeforeErase = painter.mode;
            painter.mode = ToEraseMode(modeBeforeErase);
            erasing = true;
        }

        void ExitEraseMode()
        {
            if (!erasing) return;
            painter.mode = modeBeforeErase;
            erasing = false;
        }

        static QuickTileRuntimePainter.PaintMode ToEraseMode(QuickTileRuntimePainter.PaintMode m)
        {
            switch (m)
            {
                case QuickTileRuntimePainter.PaintMode.Tile:    return QuickTileRuntimePainter.PaintMode.EraseTile;
                case QuickTileRuntimePainter.PaintMode.Texture: return QuickTileRuntimePainter.PaintMode.EraseTexture;
                case QuickTileRuntimePainter.PaintMode.Object:  return QuickTileRuntimePainter.PaintMode.EraseObject;
                default: return m;
            }
        }

        void UpdatePreview()
        {
            if (!showPreview)
            {
                if (previewGO != null) previewGO.SetActive(false);
                return;
            }

            EnsurePreview();
            previewGO.SetActive(hasCell);
            if (!hasCell) return;

            Vector3 world = painter.editor.GetPlacementWorldPos(currentCell);
            previewGO.transform.position = world + Vector3.up * 0.01f;

            int r = Mathf.Max(1, painter.brushSize);
            float side = r * 2f - 1f;
            previewGO.transform.localScale = new Vector3(side, 0.02f, side);

            if (previewRenderer != null && previewRenderer.sharedMaterial != null)
                previewRenderer.sharedMaterial.color = previewColor;
        }

        void EnsurePreview()
        {
            if (previewGO != null) return;
            previewGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
            previewGO.name = "QuickTile Runtime Brush Preview";
            previewGO.hideFlags = HideFlags.DontSave;
            var col = previewGO.GetComponent<Collider>();
            if (col != null) Destroy(col);
            previewRenderer = previewGO.GetComponent<Renderer>();
            if (previewRenderer != null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit")
                          ?? Shader.Find("Unlit/Color")
                          ?? Shader.Find("Standard");
                var mat = new Material(shader) { color = previewColor };
                mat.hideFlags = HideFlags.DontSave;
                previewRenderer.sharedMaterial = mat;
            }
        }
    }
}
