using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mapster.Common.MemoryMappedTypes;

/// <summary>
///     Action to be called when iterating over <see cref="MapFeature" /> in a given bounding box via a call to
///     <see cref="DataFile.ForeachFeature" />
/// </summary>
/// <param name="feature">The current <see cref="MapFeature" />.</param>
/// <param name="label">The label of the feature, <see cref="string.Empty" /> if not available.</param>
/// <param name="coordinates">The coordinates of the <see cref="MapFeature" />.</param>
/// <returns></returns>
public delegate bool MapFeatureDelegate(MapFeatureData featureData);

/// <summary>
///     Aggregation of all the data needed to render a map feature
/// </summary>
public readonly ref struct MapFeatureData
{
    public long Id { get; init; }

    public GeometryType Type { get; init; }
    public ReadOnlySpan<char> Label { get; init; }
    public ReadOnlySpan<Coordinate> Coordinates { get; init; }
    //Pentru a putea itera mai usor utilizam un enum la care assignam int-uri la string-uri
    public enum LandTypes
    {
        Highway = 0,
        Water = 1,
        Boundary = 2,
        Admin_level = 3,
        Place = 4,
        Railway = 5,
        Natural = 6,
        Landuse = 7,
        Building = 8,
        Leisure = 9,
        Amenity = 10,
        Name = 11,
    }
    public Dictionary<LandTypes, string> Properties { get; init; }
}


/// <summary>
///     Represents a file with map data organized in the following format:<br />
///     <see cref="FileHeader" /><br />
///     Array of <see cref="TileHeaderEntry" /> with <see cref="FileHeader.TileCount" /> records<br />
///     Array of tiles, each tile organized:<br />
///     <see cref="TileBlockHeader" /><br />
///     Array of <see cref="MapFeature" /> with <see cref="TileBlockHeader.FeaturesCount" /> at offset
///     <see cref="TileHeaderEntry.OffsetInBytes" /> + size of <see cref="TileBlockHeader" /> in bytes.<br />
///     Array of <see cref="Coordinate" /> with <see cref="TileBlockHeader.CoordinatesCount" /> at offset
///     <see cref="TileBlockHeader.CharactersOffsetInBytes" />.<br />
///     Array of <see cref="StringEntry" /> with <see cref="TileBlockHeader.StringCount" /> at offset
///     <see cref="TileBlockHeader.StringsOffsetInBytes" />.<br />
///     Array of <see cref="char" /> with <see cref="TileBlockHeader.CharactersCount" /> at offset
///     <see cref="TileBlockHeader.CharactersOffsetInBytes" />.<br />
/// </summary>
public unsafe class DataFile : IDisposable
{
    private readonly FileHeader* _fileHeader;
    private readonly MemoryMappedViewAccessor _mma;
    private readonly MemoryMappedFile _mmf;

    private readonly byte* _ptr;
    private readonly int CoordinateSizeInBytes = Marshal.SizeOf<Coordinate>();
    private readonly int FileHeaderSizeInBytes = Marshal.SizeOf<FileHeader>();
    private readonly int MapFeatureSizeInBytes = Marshal.SizeOf<MapFeature>();
    private readonly int StringEntrySizeInBytes = Marshal.SizeOf<StringEntry>();
    private readonly int TileBlockHeaderSizeInBytes = Marshal.SizeOf<TileBlockHeader>();
    private readonly int TileHeaderEntrySizeInBytes = Marshal.SizeOf<TileHeaderEntry>();

    private bool _disposedValue;

    public DataFile(string path)
    {
        _mmf = MemoryMappedFile.CreateFromFile(path);
        _mma = _mmf.CreateViewAccessor();
        _mma.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
        _fileHeader = (FileHeader*)_ptr;
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _mma?.SafeMemoryMappedViewHandle.ReleasePointer();
                _mma?.Dispose();
                _mmf?.Dispose();
            }

            _disposedValue = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private TileHeaderEntry* GetNthTileHeader(int i)
    {
        return (TileHeaderEntry*)(_ptr + i * TileHeaderEntrySizeInBytes + FileHeaderSizeInBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private (TileBlockHeader? Tile, ulong TileOffset) GetTile(int tileId)
    {
        ulong tileOffset = 0;
        for (var i = 0; i < _fileHeader->TileCount; ++i)
        {
            var tileHeaderEntry = GetNthTileHeader(i);
            if (tileHeaderEntry->ID == tileId)
            {
                tileOffset = tileHeaderEntry->OffsetInBytes;
                return (*(TileBlockHeader*)(_ptr + tileOffset), tileOffset);
            }
        }

        return (null, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private MapFeature* GetFeature(int i, ulong offset)
    {
        return (MapFeature*)(_ptr + offset + TileBlockHeaderSizeInBytes + i * MapFeatureSizeInBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private ReadOnlySpan<Coordinate> GetCoordinates(ulong coordinateOffset, int ithCoordinate, int coordinateCount)
    {
        return new ReadOnlySpan<Coordinate>(_ptr + coordinateOffset + ithCoordinate * CoordinateSizeInBytes, coordinateCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void GetString(ulong stringsOffset, ulong charsOffset, int i, out ReadOnlySpan<char> value)
    {
        var stringEntry = (StringEntry*)(_ptr + stringsOffset + i * StringEntrySizeInBytes);
        value = new ReadOnlySpan<char>(_ptr + charsOffset + stringEntry->Offset * 2, stringEntry->Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void GetProperty(ulong stringsOffset, ulong charsOffset, int i, out ReadOnlySpan<char> key, out ReadOnlySpan<char> value)
    {
        if (i % 2 != 0)
        {
            throw new ArgumentException("Properties are key-value pairs and start at even indices in the string list (i.e. i % 2 == 0)");
        }

        GetString(stringsOffset, charsOffset, i, out key);
        GetString(stringsOffset, charsOffset, i + 1, out value);
    }

    public static string UpperC(string input) =>
        input switch
        {
            null => throw new ArgumentNullException(nameof(input)),
            "" => throw new ArgumentException($"{nameof(input)} cannot be empty", nameof(input)),
            _ => string.Concat(input[0].ToString().ToUpper(), input.AsSpan(1))
        };

    
    public string[] LandTypes = new[] {
        "highway", "water", "boundary", "admin_level", "place", "railway", "natural", "landuse", "building", "leisure",
        "amenity", "name"
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void ForeachFeature(BoundingBox b, MapFeatureDelegate? action)
    {
        if (action == null)
        {
            return;
        }

        var tiles = TiligSystem.GetTilesForBoundingBox(b.MinLat, b.MinLon, b.MaxLat, b.MaxLon);
        for (var i = 0; i < tiles.Length; ++i)
        {
            var header = GetTile(tiles[i]);
            if (header.Tile == null)
            {
                continue;
            }
            for (var j = 0; j < header.Tile.Value.FeaturesCount; ++j)
            {
                var feature = GetFeature(j, header.TileOffset);
                var coordinates = GetCoordinates(header.Tile.Value.CoordinatesOffsetInBytes, feature->CoordinateOffset, feature->CoordinateCount);
                var isFeatureInBBox = false;

                for (var k = 0; k < coordinates.Length; ++k)
                {
                    if (b.Contains(coordinates[k]))
                    {
                        isFeatureInBBox = true;
                        break;
                    }
                }

                var label = ReadOnlySpan<char>.Empty;
                if (feature->LabelOffset >= 0)
                {
                    GetString(header.Tile.Value.StringsOffsetInBytes, header.Tile.Value.CharactersOffsetInBytes, feature->LabelOffset, out label);
                }

                if (isFeatureInBBox)
                {
                    var properties = new Dictionary<MapFeatureData.LandTypes, string>(feature->PropertyCount);
                    for (var p = 0; p < feature->PropertyCount; ++p)
                    {
                        GetProperty(header.Tile.Value.StringsOffsetInBytes, header.Tile.Value.CharactersOffsetInBytes,
                            p * 2 + feature->PropertiesOffset, out var key, out var value);
                        // Store in memory only the properties used for computation to immensely speed up the process (the number of properties is immense
                        // and enum parsing is computationally expensive, so it performs worse)
                        if (LandTypes.Contains(key.ToString()))
                        {
                            try
                            {
                                // Convert the string obtained from the file to an equivalent enum type for easier computation
                                MapFeatureData.LandTypes keyEnum =
                                    (MapFeatureData.LandTypes)Enum.Parse(
                                        typeof(MapFeatureData.LandTypes),
                                        UpperC(key.ToString()));
                                properties.Add(keyEnum, value.ToString());
                            }
                            catch (Exception e)
                            {
                                continue;
                            }
                        }
                    }

                    if (!action(new MapFeatureData
                        {
                            Id = feature->Id,
                            Label = label,
                            Coordinates = coordinates,
                            Type = feature->GeometryType,
                            Properties = properties
                        }))
                    {
                        break;
                    }
                }
            }
        }
    }
}
