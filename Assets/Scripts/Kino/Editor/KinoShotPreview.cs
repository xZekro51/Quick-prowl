// This file is part of Prowl.Kino, the virtual camera library for the Prowl Game Engine.
// Licensed under the MIT License.

using System;

using Prowl.Editor.Theming;
using Prowl.OrigamiUI;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Kino.Editor;

/// <summary>
/// Renders what a <see cref="KinoCamera"/> can see, from the live scene, into the inspector - with
/// the composition guides drawn over it.
/// </summary>
/// <remarks>
/// <para>
/// This is the answer to "what does this shot actually look like" without entering play mode and
/// without making the camera live. The scene is the real one, so lighting, geometry and the follow
/// target are all exactly what the game will show.
/// </para>
/// <para>
/// One hidden camera and one render texture are shared by every inspector, created the first time a
/// preview is drawn and thrown away when the scene changes underneath them.
/// </para>
/// </remarks>
public sealed class KinoShotPreview : IDisposable
{
    private GameObject? _rig;
    private Camera? _camera;
    private RenderTexture? _target;
    private Scene? _scene;
    private int _width;
    private int _height;
    private float _measured;

    /// <summary>Draws the shot, filling the width it is given, at 16:9.</summary>
    /// <returns>False when there is nothing to render into - no scene, or no camera to borrow.</returns>
    public bool Draw(Paper paper, string id, KinoCamera vcam, float fallbackWidth, in KinoComposition? composition)
    {
        if (vcam.IsNotValid())
            return false;

        // The panel's real width is only known after layout, so the previous frame's measurement sizes
        // this one. It settles on the first frame and the inspector redraws every frame, so the image
        // is never actually seen at the wrong aspect.
        float width = _measured > 32f ? _measured : fallbackWidth;
        int w = Maths.Max((int)width, 64);
        int h = Maths.Max((int)(width * 9f / 16f), 36);

        if (!Prepare(vcam, w, h))
            return false;

        KinoState state = vcam.State;
        Float2? subject = ResolveSubject(vcam, state, w / (float)h);

        // Captured for the draw callback, which runs after layout when the fields may already have
        // moved on to another inspector.
        RenderTexture target = _target!;
        KinoComposition? guides = composition;

        paper.Box(id)
            .Width(UnitValue.Stretch()).Height(h)
            .Rounded(6).Clip()
            .BackgroundColor(System.Drawing.Color.FromArgb(255, 16, 16, 20))
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .IsNotInteractable()
            .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
            {
                _measured = (float)r.Size.X;

                if (target.IsNotValid() || target.MainTexture.IsNotValid())
                    return;

                float rx = (float)r.Min.X;
                float ry = (float)r.Min.Y;
                float rw = (float)r.Size.X;
                float rh = (float)r.Size.Y;

                // Flipped vertically: the render target has Y=0 at the bottom, the canvas at the top.
                canvas.SetBrushTexture(target.MainTexture);
                canvas.SetBrushTextureTransform(
                    Vector.Spatial.Transform2D.CreateTranslation(rx, ry + rh) *
                    Vector.Spatial.Transform2D.CreateScale(rw, -rh));
                canvas.RoundedRectFilled(rx, ry, rw, rh, 6, 6, 6, 6, new Color32(255, 255, 255, 255));
                canvas.ClearBrushTexture();

                if (guides.HasValue)
                    KinoCompositionGuide.Draw(canvas, r, guides.Value, subject);
            }));

        return true;
    }

    /// <summary>Where the subject lands on screen in this shot, or null when it is not in view.</summary>
    private static Float2? ResolveSubject(KinoCamera vcam, in KinoState state, float aspect)
    {
        Float3 point;
        if (vcam.HasLookAt) point = vcam.LookAtPosition;
        else if (vcam.HasFollow) point = vcam.FollowPosition;
        else return null;

        return KinoScreen.TargetToNdc(state.FinalPosition, state.FinalRotation, point, state.Lens, aspect,
            out Float2 ndc, out _)
            ? ndc
            : null;
    }

    /// <summary>
    /// Points the borrowed camera at the shot and renders the scene through it. The camera lives in
    /// the scene being edited - that is the whole point, it has to see the real world - but is hidden
    /// and never saved.
    /// </summary>
    private bool Prepare(KinoCamera vcam, int width, int height)
    {
        Scene? scene = vcam.GameObject.IsValid() ? vcam.GameObject.Scene : null;
        if (scene.IsNotValid())
            return false;

        if (!ReferenceEquals(scene, _scene))
        {
            // A scene swap takes the old rig with it; anything left pointing at it would render into
            // a world that no longer exists.
            Release();
            _scene = scene;
        }

        if (_rig.IsNotValid() || _camera.IsNotValid())
        {
            _rig = new GameObject("Kino Shot Preview") { HideFlags = HideFlags.HideAndDontSave | HideFlags.NoGizmos };
            _camera = _rig.AddComponent<Camera>();
            _camera.Depth = int.MinValue; // never competes with the game's own cameras
            scene.Add(_rig);
        }

        if (_target.IsNotValid() || _width != width || _height != height)
        {
            if (_target.IsValid()) _target.Dispose();
            _width = width;
            _height = height;
            _target = new RenderTexture(width, height, true, new[] { TextureImageFormat.Color4b });
        }

        // Evaluated here rather than read: with no brain in the scene nothing else would ever run this
        // camera's pipeline, and the preview would be looking at a shot that was never worked out.
        KinoState state = vcam.Evaluate(width / (float)height);
        _rig.Transform.Position = state.FinalPosition;
        _rig.Transform.Rotation = state.FinalRotation;
        state.Lens.ApplyTo(_camera);
        _camera.Aspect = width / (float)height;

        try
        {
            _camera.UpdateRenderData(_target);
            RenderPipeline pipeline = _camera.Pipeline.IsValid() ? _camera.Pipeline : DefaultRenderPipeline.Default;
            pipeline.Render(_camera, new RenderingData { FallbackTarget = _target });
        }
        catch (Exception e)
        {
            // A preview is never worth taking the editor down for.
            Debug.LogWarning($"[Kino] Shot preview could not be rendered: {e.Message}");
            return false;
        }

        return true;
    }

    private void Release()
    {
        if (_rig.IsValid())
            _rig.Destroy();

        _rig = null;
        _camera = null;

        if (_target.IsValid()) _target.Dispose();
        _target = null;
        _width = _height = 0;
    }

    public void Dispose() => Release();
}
