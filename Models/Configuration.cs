using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace MyFences.Models;

public sealed class Configuration
{
    public int Version { get; set; } = 1;
    public List<FenceState> Fences { get; set; } = new();
}

public sealed class FenceState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Новый блок";
    // Координаты экрана и размеры в физических пикселях.
    public int X { get; set; } = 100;
    public int Y { get; set; } = 100;
    public int Width { get; set; } = 380;
    public int Height { get; set; } = 340;
    public bool RolledUp { get; set; }
}

public sealed class FenceItem
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public ImageSource? Icon { get; init; }
}
