namespace DiscImageStudio.Cd;

/// <summary>
/// Physical rings of a 120 mm CD-R as seen from the burned side, in millimetres of radius.
/// Fixed features follow ECMA-130 / IEC 60908 and the Orange Book (CD-R); the program area comes
/// from the selected disc's measured geometry, and the lead-out from the 90 s a disc-at-once
/// burn writes after the last data sector.
/// </summary>
public sealed record CdPhysicalLayout(
    double DiscRadiusMm,
    double CenterHoleRadiusMm,
    double ClampingInnerRadiusMm,
    double ClampingOuterRadiusMm,
    double StackingRingInnerRadiusMm,
    double StackingRingOuterRadiusMm,
    double DyeInnerRadiusMm,
    double PowerCalibrationRadiusMm,
    double LeadInRadiusMm,
    double ProgramStartRadiusMm,
    double ProgramEndRadiusMm,
    double LeadOutEndRadiusMm,
    double DyeOuterRadiusMm)
{
    /// <summary>Disc-at-once lead-out: 1 min 30 s.</summary>
    public const long LeadOutSectors = 90 * 75;

    public static CdPhysicalLayout For(CdDiscParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        double programEnd = parameters.OutputOuterRadiusMm;
        // Same constant-area-per-sector law the geometry uses, continued past the program area.
        double areaPerSector = (parameters.OuterRadiusMm * parameters.OuterRadiusMm
            - parameters.InnerRadiusMm * parameters.InnerRadiusMm) / parameters.Sectors;
        double leadOutEnd = Math.Sqrt(programEnd * programEnd + areaPerSector * LeadOutSectors);
        const double dyeOuter = 59.0;
        return new CdPhysicalLayout(
            DiscRadiusMm: 60.0,                 // Ø120 mm
            CenterHoleRadiusMm: 7.5,            // Ø15 mm
            ClampingInnerRadiusMm: 13.0,        // Ø26 mm
            ClampingOuterRadiusMm: 16.5,        // Ø33 mm
            StackingRingInnerRadiusMm: 16.6,    // Ø33.2 mm (moulded ring; position varies slightly by maker)
            StackingRingOuterRadiusMm: 17.1,    // Ø34.2 mm
            DyeInnerRadiusMm: 22.0,             // Ø44 mm: dye coating begins
            PowerCalibrationRadiusMm: 22.35,    // Ø44.7 mm: PCA, then PMA
            LeadInRadiusMm: 23.0,               // Ø46 mm: lead-in starts
            ProgramStartRadiusMm: parameters.InnerRadiusMm,
            ProgramEndRadiusMm: programEnd,
            LeadOutEndRadiusMm: Math.Min(leadOutEnd, dyeOuter),
            DyeOuterRadiusMm: dyeOuter);         // Ø118 mm: dye coating ends, clear rim to the edge
    }
}
