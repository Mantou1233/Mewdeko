﻿using System.Linq;
using Mewdeko._Extensions;
using Mewdeko.Services.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace Mewdeko.Services.Database.Repositories.Impl;

public class PlantedCurrencyRepository : Repository<PlantedCurrency>, IPlantedCurrencyRepository
{
    public PlantedCurrencyRepository(DbContext context) : base(context)
    {
    }

    public decimal GetTotalPlanted()
    {
        return _set.Sum(x => x.Amount);
    }

    public (long Sum, ulong[] MessageIds) RemoveSumAndGetMessageIdsFor(ulong cid, string pass = null)
    {
        pass = pass?.Trim().TrimTo(10, true).ToUpperInvariant();
        // gets all plants in this channel with the same password
        var entries = _set.AsQueryable().Where(x => x.ChannelId == cid && pass == x.Password).ToArray();
        // sum how much currency that is, and get all of the message ids (so that i can delete them)
        var toReturn = (entries.Sum(x => x.Amount), entries.Select(x => x.MessageId).ToArray());
        // remove them from the database
        _set.RemoveRange(entries);

        return toReturn;
    }
}