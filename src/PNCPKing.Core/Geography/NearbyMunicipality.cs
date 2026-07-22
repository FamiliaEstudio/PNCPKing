namespace PNCPKing.Core.Geography;

public sealed record NearbyMunicipality(
    string IbgeCode,
    string Name,
    string Uf,
    double Latitude,
    double Longitude,
    double DistanceFromRibeiraoKilometers);
