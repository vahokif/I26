using Godot;
using I76.Formats.Vfs;

namespace I76.Game.Boot;

/// <summary>The map editor's part of the main scene (game/Scripts/Editor, docs/map-editor.md).</summary>
public partial class Main
{
    /// <summary>The mission is built and this scene is the editor: its camera, brushes and windows over the world.</summary>
    private void StartEditor()
    {
        var cam = (I76.Game.Editor.EditorCamera)_cam;
        cam.Origin = () => _origin;
        cam.Rebase = Rebase;
        cam.MakeCurrent();
        _editor = new I76.Game.Editor.MapEditor
        {
            Name = "MapEditor", Builder = _builder!, Doc = I76.Game.Editor.EditorSession.Doc!, Cam = cam, Undo = I76.Game.Editor.EditorSession.Undo!,
            Origin = () => _origin, ToggleLook = ToggleLook, Is1997 = () => _mode1997, VegetationOf = () => _veg,
            Reload = () => { ReleaseCursor(); GetTree().ReloadCurrentScene(); },
            Leave = LeaveEditor, Install = new GameInstall(_opt.Install!),
        };
        AddChild(_editor);
        GD.Print($"map editor: {_opt.Mission}");
    }

    /// <summary>Exit editor: back to the shell, or the end of the program when it was started on the mission.</summary>
    private void LeaveEditor()
    {
        ReleaseCursor();
        if (_cmdLine) { GetTree().Quit(); return; }
        _backToMenu = true;
        GetTree().ReloadCurrentScene();
    }
}
