using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using FModel.Settings;
using FModel.Views.Snooper.Animations;
using FModel.Views.Snooper.Lights;
using FModel.Views.Snooper.Models;
using FModel.Views.Snooper.Shading;
using SkiaSharp;
using OpenTK.Graphics.OpenGL4;

namespace FModel.Views.Snooper;

public class Options
{
    public FGuid SelectedModel { get; private set; }
    public int SelectedSection { get; private set; }
    public int SelectedMorph { get; private set; }
    public int SelectedAnimation{ get; private set; }

    public readonly Dictionary<FGuid, Model> Models;
    public readonly Dictionary<FGuid, Texture> Textures;
    public readonly List<Light> Lights;

    public readonly TimeTracker Tracker;
    public readonly List<Animation> Animations;

    public readonly Dictionary<string, Texture> Icons;

    private readonly ETexturePlatform _platform;
    private readonly string _game;

    public Options()
    {
        Models = new Dictionary<FGuid, Model>();
        Textures = new Dictionary<FGuid, Texture>();
        Lights = new List<Light>();

        Tracker = new TimeTracker();
        Animations = new List<Animation>();

        Icons = new Dictionary<string, Texture>
        {
            ["material"] = new ("materialicon"),
            ["noimage"] = new ("T_Placeholder_Item_Image"),
            ["pointlight"] = new ("pointlight"),
            ["spotlight"] = new ("spotlight"),
            ["link_on"] = new ("link_on"),
            ["link_off"] = new ("link_off"),
            ["link_has"] = new ("link_has"),
            ["tl_play"] = new ("tl_play"),
            ["tl_pause"] = new ("tl_pause"),
            ["tl_rewind"] = new ("tl_rewind"),
            ["tl_forward"] = new ("tl_forward"),
            ["tl_previous"] = new ("tl_previous"),
            ["tl_next"] = new ("tl_next"),
        };

        _platform = UserSettings.Default.OverridedPlatform;
        _game = Services.ApplicationService.ApplicationView.CUE4Parse.Provider.GameName.ToUpper();

        SelectModel(Guid.Empty);
    }

    public void SetupModelsAndLights()
    {
        foreach (var model in Models.Values)
        {
            if (model.IsSetup) continue;
            model.Setup(this);
        }

        foreach (var light in Lights)
        {
            if (light.IsSetup) continue;
            light.Setup();
        }
    }

    public void SelectModel(FGuid guid)
    {
        // unselect old
        if (TryGetModel(out var model))
            model.IsSelected = false;

        // select new
        if (!TryGetModel(guid, out model))
            SelectedModel = Guid.Empty;
        else
        {
            model.IsSelected = true;
            SelectedModel = guid;
        }

        SelectedSection = 0;
        SelectedMorph = 0;
    }

    public void SelectAnimation(int animation)
    {
        SelectedAnimation = animation;
    }

    public void RemoveModel(FGuid guid)
    {
        if (!TryGetModel(guid, out var model)) return;

        DetachAndRemoveModels(model, true);
        model.Dispose();
        Models.Remove(guid);
    }

    private void DetachAndRemoveModels(Model model, bool detach)
    {
        foreach (var socket in model.Sockets.ToList())
        {
            foreach (var info in socket.AttachedModels)
            {
                if (!TryGetModel(info.Guid, out var attachedModel)) continue;

                if (attachedModel.IsAnimatedProp)
                {
                    attachedModel.SafeDetachModel(model);
                    RemoveModel(info.Guid);
                }
                else if (detach) attachedModel.SafeDetachModel(model);
            }

            if (socket.IsVirtual)
            {
                socket.Dispose();
                model.Sockets.Remove(socket);
            }
        }
    }

    public void AddAnimation(Animation animation)
    {
        Animations.Add(animation);
    }

    public void RemoveAnimations()
    {
        Tracker.Reset();
        SelectedAnimation = 0;

        foreach (var animation in Animations)
        {
            foreach (var guid in animation.AttachedModels)
            {
                if (!TryGetModel(guid, out var animatedModel)) continue;

                animatedModel.Skeleton.ResetAnimatedData(true);
                DetachAndRemoveModels(animatedModel, false);
            }
            animation.Dispose();
        }
        foreach (var kvp in Models.ToList().Where(kvp => kvp.Value.IsAnimatedProp))
        {
            RemoveModel(kvp.Key);
        }
        Animations.Clear();
    }

    public void SelectSection(int index)
    {
        SelectedSection = index;
    }

    public void SelectMorph(int index, Model model)
    {
        SelectedMorph = index;
        model.UpdateMorph(SelectedMorph);
    }

    public bool TryGetTexture(UTexture2D o, bool fix, out Texture texture)
    {
        var guid = o.LightingGuid;
        if (!Textures.TryGetValue(guid, out texture))
        {
            if (o.GetMipByMaxSize(UserSettings.Default.PreviewMaxTextureSize) is { } mip)
            {
                TextureDecoder.DecodeTexture(mip, o.Format, o.IsNormalMap, _platform, out var data, out _);

                texture = new Texture(data, mip.SizeX, mip.SizeY, o);
                if (fix)
                    TextureHelper.FixChannels(_game, texture);
                Textures[guid] = texture;
            }
            else if (o.IsVirtual && o.PlatformData.VTData is FVirtualTextureBuiltData vtdata && TextureDecoder.Decode(o, vtdata, _platform) is SKBitmap bitmap)
            {
                texture = new Texture(bitmap.Bytes, bitmap.Width, bitmap.Height, o, bitmap.ColorType switch
                {
                    SKColorType.Rgb888x => PixelFormat.Rgb,
                    SKColorType.Rgb565 => PixelFormat.Rgb,
                    SKColorType.Bgra8888 => PixelFormat.Bgra,
                    SKColorType.Gray8 => PixelFormat.Luminance,
                    _ => PixelFormat.Rgba
                }, bitmap.ColorType == SKColorType.Rgb565 ? PixelType.UnsignedShort565 : PixelType.UnsignedByte);
                bitmap.Dispose();
                if (fix)
                    TextureHelper.FixChannels(_game, texture);
                Textures[guid] = texture;
            }
        }
        return texture != null;
    }

    public bool TryGetModel(out Model model) => Models.TryGetValue(SelectedModel, out model);
    public bool TryGetModel(FGuid guid, out Model model) => Models.TryGetValue(guid, out model);

    public bool TryGetSection(out Section section) => TryGetSection(SelectedModel, out section);
    public bool TryGetSection(FGuid guid, out Section section)
    {
        if (TryGetModel(guid, out var model))
        {
            return TryGetSection(model, out section);
        }

        section = null;
        return false;
    }
    public bool TryGetSection(Model model, out Section section)
    {
        if (SelectedSection >= 0 && SelectedSection < model.Sections.Length)
            section = model.Sections[SelectedSection]; else section = null;
        return section != null;
    }

    public void SwapMaterial(bool value)
    {
        Services.ApplicationService.ApplicationView.CUE4Parse.ModelIsOverwritingMaterial = value;
    }

    public void AnimateMesh(bool value)
    {
        Services.ApplicationService.ApplicationView.CUE4Parse.ModelIsWaitingAnimation = value;
    }

    public bool TrySave(UObject export, out string label, out string savedFilePath)
    {
        var exportOptions = new ExporterOptions
        {
            LodFormat = UserSettings.Default.LodExportFormat,
            MeshFormat = UserSettings.Default.MeshExportFormat,
            MaterialFormat = UserSettings.Default.MaterialExportFormat,
            TextureFormat = UserSettings.Default.TextureExportFormat,
            SocketFormat = UserSettings.Default.SocketExportFormat,
            Platform = UserSettings.Default.OverridedPlatform,
            ExportMorphTargets = UserSettings.Default.SaveMorphTargets
        };
        var toSave = new Exporter(export, exportOptions);
        return toSave.TryWriteToDir(new DirectoryInfo(UserSettings.Default.ModelDirectory), out label, out savedFilePath);
    }

    public void ResetModelsLightsAnimations()
    {
        foreach (var model in Models.Values)
        {
            model.Dispose();
        }
        Models.Clear();
        Lights.Clear();
        Tracker.Reset();
        foreach (var animation in Animations)
        {
            animation.Dispose();
        }
        Animations.Clear();
    }

    public void Dispose()
    {
        ResetModelsLightsAnimations();
        foreach (var texture in Textures.Values)
        {
            texture.Dispose();
        }
        Textures.Clear();
        foreach (var texture in Icons.Values)
        {
            texture.Dispose();
        }
        Icons.Clear();
    }
}
