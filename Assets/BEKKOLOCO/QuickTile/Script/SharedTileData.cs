// SharedTileData.cs
// Contains shared data types used across QuickTile components

using UnityEngine;

namespace Bekkoloco
{
    /// <summary>
    /// Represents a tile tracked with its position and associated rule.
    /// This is a shared data type accessible to both runtime and editor code.
    /// </summary>
    [System.Serializable]
    public class TrackedTile
    {
        public Vector3Int position;
        public QuickTilemapEditor.TileRule rule;
    }
}
