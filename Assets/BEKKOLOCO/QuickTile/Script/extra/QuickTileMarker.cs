// Assets/BEKKOLOCO/QuickTile/Script/extra/QuickTileMarker.cs
using UnityEngine;

namespace Bekkoloco
{
    [DisallowMultipleComponent]
    public class QuickTileMarker : MonoBehaviour
    {
        [SerializeField] private string _placedObjectId;
        [SerializeField] private string _editorInstanceId;
        [SerializeField] private int _ruleIndex;
        [SerializeField] private Vector3Int _gridPosition;

        public string PlacedObjectId => _placedObjectId;
        public string EditorInstanceId => _editorInstanceId;
        public int RuleIndex => _ruleIndex;
        public Vector3Int GridPosition => _gridPosition;

        public void Initialize(string placedObjectId, string editorInstanceId, int ruleIndex, Vector3Int gridPos)
        {
            _placedObjectId = placedObjectId;
            _editorInstanceId = editorInstanceId;
            _ruleIndex = ruleIndex;
            _gridPosition = gridPos;
        }
    }
}
