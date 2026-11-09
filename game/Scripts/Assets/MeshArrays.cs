using Godot;

namespace I76.Game;

/// <summary>
/// Engine calls that take the engine's collections (Godot.Collections.Array and Dictionary), made so that the managed wrappers
/// outlive the call.
///
/// The engine's C# bindings hand a collection's native value to the engine and keep no reference to the wrapper meanwhile, and
/// an argument left at its default is a wrapper made just for the call. In an optimised build (an export's: its GodotSharp is
/// the release one, and so is the game's own code) such a wrapper is garbage as soon as its native value is read, so a
/// collection on any thread during the engine call can finalise it and free the native array while the engine still reads
/// it: the mesh upload failed ("Condition "!success" is true" in Array::_ref as the engine copied an argument already freed,
/// or "p_arrays.size() != ARRAY_MAX") and the heap was left corrupt, a crash soon after. The map
/// editor's big strokes made it likely: chunk meshes uploaded every frame while the workers' new meshes kept the
/// collector busy. A development run never shows it, as the editor's own GodotSharp is a debug build that keeps every
/// argument alive to the end of the call.
/// </summary>
public static class MeshArrays
{
    /// <summary>
    /// <see cref="ArrayMesh.AddSurfaceFromArrays"/> with no blend shapes and no LODs, the arrays and both empty collections held
    /// until the engine has read them. The arrays are the caller's still (not disposed here).
    /// </summary>
    public static void AddSurfaceArrays(this ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays, Mesh.ArrayFormat flags = 0)
    {
        var blendShapes = new Godot.Collections.Array<Godot.Collections.Array>();
        var lods = new Godot.Collections.Dictionary();
        mesh.AddSurfaceFromArrays(primitive, arrays, blendShapes, lods, flags);
        System.GC.KeepAlive(arrays);
        System.GC.KeepAlive(blendShapes);
        lods.Dispose();
    }
}

/// <summary>
/// Debug builds' check that engine work is on the main thread (the map editor's stroke path runs its mesh building, surface
/// refresh and plant placement on workers as plain arrays; only the uploads touch the engine). Nothing in a release build.
/// </summary>
public static class MainThread
{
    private static int _id;

    /// <summary>Called once on the main thread (Main._Ready).</summary>
    public static void Mark() => _id = System.Environment.CurrentManagedThreadId;

    /// <summary>
    /// This thread builds a scene of its own outside the tree (a mission built under its briefing, Main.BuildUnderBriefing): its
    /// nodes and resources are the engine's to make on any thread while no other thread can reach them, so the check passes.
    /// </summary>
    [System.ThreadStatic] public static bool OwnsItsScene;

    [System.Diagnostics.Conditional("DEBUG")]
    public static void Check(string what)
    {
        if (_id != 0 && System.Environment.CurrentManagedThreadId != _id && !OwnsItsScene)
            throw new System.InvalidOperationException($"{what}: engine work off the main thread (thread {System.Environment.CurrentManagedThreadId})");
    }
}
