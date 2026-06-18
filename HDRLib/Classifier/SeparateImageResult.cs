// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Classifier;

public class SeparateImageResult
{
    public List<List<string>> HdrSeries { get; set; } = new();

    public List<string> SingleImages { get; set; } = new();
}
