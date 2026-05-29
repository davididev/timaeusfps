using UnityEngine;
using UnityEditor;

namespace Bekkoloco
{
    [CustomEditor(typeof(PathFollower))]
    public class PathFollowerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var follower = (PathFollower)target;
            if (GUILayout.Button("Start Moving"))
                follower.StartMoving();
        }
    }
}
