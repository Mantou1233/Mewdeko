﻿using Mewdeko.Database.Models;

namespace Mewdeko.Database.Common;

public readonly struct StreamDataKey
{
    public FollowedStream.FType Type { get; }
    public string Name { get; }

    public StreamDataKey(FollowedStream.FType type, string name)
    {
        Type = type;
        Name = name;
    }
}