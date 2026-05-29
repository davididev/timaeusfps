import os

base_dir = "/Users/richy/Documents/UNITY/Super Rescue rush/Assets/BEKKOLOCO/QuickTile/Script/Editor"
part1 = os.path.join(base_dir, "QuickTilemapEditorInspector_GridUI.cs")
part2 = os.path.join(base_dir, "QuickTilemapEditorInspector_GridUI.cs.part2")
part3 = os.path.join(base_dir, "QuickTilemapEditorInspector_GridUI.cs.part3")
final = os.path.join(base_dir, "QuickTilemapEditorInspector_GridUI_Merged.cs")

print(f"Merging files into {final}...")

try:
    with open(final, "w") as outfile:
        # Part 1
        if os.path.exists(part1):
            with open(part1, "r") as infile:
                outfile.write(infile.read())
        else:
            print("Error: Part 1 missing")
        
        # Part 2
        if os.path.exists(part2):
            with open(part2, "r") as infile:
                outfile.write(infile.read())
        else:
            print("Error: Part 2 missing")
            
        # Part 3
        if os.path.exists(part3):
            with open(part3, "r") as infile:
                outfile.write(infile.read())
        else:
            print("Error: Part 3 missing")

    print("Success. Renaming...")
    # Rename merged to original
    os.replace(final, part1)
    
    # Cleanup parts
    if os.path.exists(part2): os.remove(part2)
    if os.path.exists(part3): os.remove(part3)
    
    print("Merge complete.")

except Exception as e:
    print(f"Error during merge: {e}")
