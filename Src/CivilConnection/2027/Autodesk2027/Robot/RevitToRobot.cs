// Copyright (c) 2016 Autodesk, Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Structure.StructuralSections;
using RobotOM;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CivilConnection.Robot
{
    /// <summary>
    /// Sends Revit structural elements to Robot. Uses the analytical model when it exists and falls back to the
    /// physical geometry otherwise: location lines of framing, base to top of columns, top face of floors and
    /// straight structural walls. Depends on the Revit API only (no Dynamo).
    /// </summary>
    internal class RevitToRobot
    {
        private readonly Document _doc;
        private readonly RobotLink _link;
        private readonly Func<XYZ, double[]> _toRobot;
        private readonly AnalyticalToPhysicalAssociationManager _association;

        internal List<long> ElementIds { get; } = new List<long>();
        internal List<List<int>> Bars { get; } = new List<List<int>>();
        internal List<int> Panels { get; } = new List<int>();
        internal List<string> Source { get; } = new List<string>();
        internal List<long> Skipped { get; } = new List<long>();

        /// <param name="doc">Revit document.</param>
        /// <param name="link">Robot connection.</param>
        /// <param name="toRobot">Converts a point in Revit internal units to Robot coordinates in meters.</param>
        internal RevitToRobot(Document doc, RobotLink link, Func<XYZ, double[]> toRobot)
        {
            _doc = doc;
            _link = link;
            _toRobot = toRobot;
            _association = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
        }

        internal void Send(IEnumerable<ElementId> ids, string defaultSection, double maxSegmentLength, double meshSize)
        {
            foreach (ElementId id in ids)
            {
                Element element = _doc.GetElement(id);
                if (element == null) continue;

                Element analytical = element as AnalyticalElement;
                if (analytical == null && _association != null)
                {
                    ElementId aid = _association.GetAssociatedElementId(id);
                    if (aid != null && aid != ElementId.InvalidElementId) analytical = _doc.GetElement(aid);
                }

                if (analytical is AnalyticalMember member)
                {
                    string material = Material(member.MaterialId);
                    var physical = _association == null ? null : _doc.GetElement(_association.GetAssociatedElementId(member.Id)) as FamilyInstance;
                    Curve curve = member.GetCurve();
                    XYZ up = physical == null ? null : SectionUp(physical, (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize());
                    Releases(member, out bool[] relStart, out bool[] relEnd);
                    AddBars(id, "AnalyticalMember", Divide(curve, maxSegmentLength), Section(member.SectionTypeId, material, defaultSection, physical), material, up, relStart, relEnd);
                }
                else if (analytical is AnalyticalPanel panel)
                {
                    string material = Material(panel.MaterialId);
                    AddPanel(id, "AnalyticalPanel", Contour(panel.GetOuterContour(), maxSegmentLength), Thickness(panel.Thickness, material), meshSize);
                }
                else if (element is FamilyInstance fi && fi.StructuralType != StructuralType.NonStructural && fi.StructuralType != StructuralType.Footing)
                {
                    string material = Material(fi.StructuralMaterialId);
                    string section = Section(fi.Symbol.Id, material, defaultSection, fi);
                    IList<XYZ> points = PhysicalAxis(fi, maxSegmentLength);
                    if (points == null) Skipped.Add(id.GetValue());
                    else AddBars(id, "Physical" + fi.StructuralType, points, section, material, SectionUp(fi, (points[points.Count - 1] - points[0]).Normalize()), null, null);
                }
                else if (element is Floor floor)
                {
                    PlanarFace top = HostObjectUtils.GetTopFaces(floor).Select(r => floor.GetGeometryObjectFromReference(r)).OfType<PlanarFace>().OrderByDescending(f => f.Area).FirstOrDefault();
                    double thickness = floor.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)?.AsDouble() ?? 0;
                    if (top == null || thickness <= 0) { Skipped.Add(id.GetValue()); continue; }
                    CurveLoop outer = top.GetEdgesAsCurveLoops().OrderByDescending(l => l.GetExactLength()).First();
                    string material = Material(CompoundMaterial(floor.FloorType.GetCompoundStructure()));
                    AddPanel(id, "PhysicalFloor", Contour(outer, maxSegmentLength), Thickness(thickness, material), meshSize);
                }
                else if (element is Wall wall && wall.StructuralUsage != StructuralWallUsage.NonBearing && wall.Location is LocationCurve wlc && wlc.Curve is Line line)
                {
                    double baseZ = (_doc.GetElement(wall.LevelId) as Level)?.Elevation ?? 0;
                    baseZ += wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
                    double height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
                    if (height <= 0) { Skipped.Add(id.GetValue()); continue; }
                    XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
                    var contour = new List<double[]>
                    {
                        _toRobot(new XYZ(p0.X, p0.Y, baseZ)), _toRobot(new XYZ(p1.X, p1.Y, baseZ)),
                        _toRobot(new XYZ(p1.X, p1.Y, baseZ + height)), _toRobot(new XYZ(p0.X, p0.Y, baseZ + height))
                    };
                    string material = Material(CompoundMaterial(wall.WallType.GetCompoundStructure()));
                    AddPanel(id, "PhysicalWall", contour, Thickness(wall.Width, material), meshSize);
                }
                else
                {
                    Skipped.Add(id.GetValue());
                }
            }
        }

        /// <param name="up">Height direction of the section (Robot local z), or null to keep the Robot default.</param>
        /// <param name="relStart">Releases at the start (UX..RZ, true = released), or null.</param>
        /// <param name="relEnd">Releases at the end, or null.</param>
        private void AddBars(ElementId id, string source, IList<XYZ> points, string section, string material, XYZ up, bool[] relStart, bool[] relEnd)
        {
            var bars = new List<int>();
            for (int i = 0; i < points.Count - 1; i++)
            {
                int bar = _link.CreateBar(_toRobot(points[i]), _toRobot(points[i + 1]), section, material);
                if (bar <= 0) continue;
                bars.Add(bar);
                if (up != null) _link.SetBarOrientation(bar, up.X, up.Y, up.Z);
            }
            if (bars.Count > 0 && (relStart != null || relEnd != null))
            {
                bool[] none = new bool[6];
                if (bars.Count == 1)
                {
                    _link.SetBarRelease(bars[0], relStart ?? none, relEnd ?? none);
                }
                else
                {
                    _link.SetBarRelease(bars[0], relStart ?? none, none);
                    _link.SetBarRelease(bars[bars.Count - 1], none, relEnd ?? none);
                }
            }
            ElementIds.Add(id.GetValue());
            Bars.Add(bars);
            Panels.Add(0);
            Source.Add(source);
        }

        private void AddPanel(ElementId id, string source, IList<double[]> contour, string thickness, double meshSize)
        {
            ElementIds.Add(id.GetValue());
            Bars.Add(new List<int>());
            Panels.Add(_link.CreatePanel(contour, thickness, meshSize));
            Source.Add(source);
        }

        /// <summary>
        /// Height direction of the section of a framing or column instance, perpendicular to the member axis.
        /// Framing: the instance transform has BasisX along the member and BasisZ as the section up direction (cross-section
        /// rotation included). Columns: BasisZ is the axis and the section height h lies along BasisY.
        /// </summary>
        internal static XYZ SectionUp(FamilyInstance fi, XYZ axis)
        {
            Transform t = fi.GetTransform();
            XYZ up = Math.Abs(t.BasisZ.DotProduct(axis)) > 0.99 ? t.BasisY : t.BasisZ;
            XYZ perpendicular = up - axis * up.DotProduct(axis);
            return perpendicular.GetLength() < 1e-6 ? null : perpendicular.Normalize();
        }

        /// <summary>
        /// End releases of an analytical member (UX, UY, UZ, RX, RY, RZ; true = released).
        /// </summary>
        private static void Releases(AnalyticalMember member, out bool[] start, out bool[] end)
        {
            start = new bool[6];
            end = new bool[6];
            foreach (ReleaseConditions rc in member.GetReleaseConditions())
            {
                var r = new[] { rc.Fx, rc.Fy, rc.Fz, rc.Mx, rc.My, rc.Mz };
                if (rc.Start) start = r; else end = r;
            }
        }

        /// <summary>
        /// Axis of a structural family instance: the location line, or base to top for vertical columns.
        /// </summary>
        private static IList<XYZ> PhysicalAxis(FamilyInstance fi, double maxSegmentLength)
        {
            if (fi.Location is LocationCurve lc) return Divide(lc.Curve, maxSegmentLength);

            if (fi.Location is LocationPoint lp && fi.StructuralType == StructuralType.Column)
            {
                Document doc = fi.Document;
                double z0 = (doc.GetElement(fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Elevation ?? double.NaN;
                double z1 = (doc.GetElement(fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level)?.Elevation ?? double.NaN;
                if (double.IsNaN(z0) || double.IsNaN(z1)) return null;
                z0 += fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0;
                z1 += fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0;
                return new List<XYZ> { new XYZ(lp.Point.X, lp.Point.Y, z0), new XYZ(lp.Point.X, lp.Point.Y, z1) };
            }
            return null;
        }

        /// <summary>
        /// Points along a curve; lines are kept whole, other curves are divided in segments no longer than maxSegmentLength (m).
        /// </summary>
        private static IList<XYZ> Divide(Curve curve, double maxSegmentLength)
        {
            if (curve is Line || maxSegmentLength <= 0) return new List<XYZ> { curve.GetEndPoint(0), curve.GetEndPoint(1) };
            double length = UnitUtils.ConvertFromInternalUnits(curve.Length, UnitTypeId.Meters);
            int n = Math.Max(1, (int)Math.Ceiling(length / maxSegmentLength));
            return Enumerable.Range(0, n + 1).Select(i => curve.Evaluate((double)i / n, true)).ToList();
        }

        private IList<double[]> Contour(CurveLoop loop, double maxSegmentLength)
        {
            var points = new List<double[]>();
            foreach (Curve c in loop)
            {
                IList<XYZ> pts = Divide(c, maxSegmentLength);
                points.AddRange(pts.Take(pts.Count - 1).Select(_toRobot));
            }
            return points;
        }

        private static ElementId CompoundMaterial(CompoundStructure cs)
        {
            if (cs == null) return ElementId.InvalidElementId;
            int index = cs.StructuralMaterialIndex;
            return index >= 0 ? cs.GetMaterialId(index) : ElementId.InvalidElementId;
        }

        private string Thickness(double thicknessInternal, string material)
        {
            double t = UnitUtils.ConvertFromInternalUnits(thicknessInternal, UnitTypeId.Meters);
            string name = string.IsNullOrEmpty(material) ? string.Format("TH{0:0}", t * 1000) : string.Format("TH{0:0}_{1}", t * 1000, material);
            if (_link.Structure.Labels.Exist(IRobotLabelType.I_LT_PANEL_THICKNESS, name) == 0)
            {
                _link.CreatePanelThickness(name, t, material, 0);
            }
            return name;
        }

        /// <summary>
        /// Robot material from the Revit material's structural asset (created once per name).
        /// </summary>
        internal string Material(ElementId materialId)
        {
            var material = _doc.GetElement(materialId) as Material;
            if (material == null) return "";

            string name = material.Name;
            if (_link.Structure.Labels.Exist(IRobotLabelType.I_LT_MATERIAL, name) != 0) return name;

            StructuralAsset sa = (_doc.GetElement(material.StructuralAssetId) as PropertySetElement)?.GetStructuralAsset();
            if (sa == null || sa.YoungModulus.X <= 0) return "";

            double e = UnitUtils.ConvertFromInternalUnits(sa.YoungModulus.X, UnitTypeId.Pascals);
            double density = UnitUtils.ConvertFromInternalUnits(sa.Density, UnitTypeId.KilogramsPerCubicMeter);
            IRobotMaterialType type = sa.StructuralAssetClass == StructuralAssetClass.Metal ? IRobotMaterialType.I_MT_STEEL
                : sa.StructuralAssetClass == StructuralAssetClass.Wood ? IRobotMaterialType.I_MT_TIMBER
                : sa.StructuralAssetClass == StructuralAssetClass.Concrete ? IRobotMaterialType.I_MT_CONCRETE
                : IRobotMaterialType.I_MT_OTHER;

            return _link.CreateMaterial(name, type, e, sa.PoissonRatio.X, density * 9.80665);
        }

        /// <summary>
        /// Robot section for a Revit family type, in this order: solid rectangle, section of the same name in the Robot
        /// database, round bar, properties stored in the Revit type, properties computed from the cross-section of the
        /// element geometry. Created once per type name.
        /// </summary>
        internal string Section(ElementId typeId, string material, string defaultSection, FamilyInstance instance)
        {
            var symbol = _doc.GetElement(typeId) as FamilySymbol;
            if (symbol == null) return defaultSection;

            StructuralSection section = symbol.GetStructuralSection();

            // Thin-walled profiles (C, U, L...) also derive from StructuralSectionRectangular: only solid rectangles go here
            if (section is StructuralSectionConcreteRectangle || section is StructuralSectionRectangularBar || section is StructuralSectionRectangleParameterized)
            {
                var rect = (StructuralSectionRectangular)section;
                double b = UnitUtils.ConvertFromInternalUnits(rect.Width, UnitTypeId.Meters);
                double h = UnitUtils.ConvertFromInternalUnits(rect.Height, UnitTypeId.Meters);
                string name = string.Format("R{0:0}x{1:0}", b * 1000, h * 1000);
                if (_link.Structure.Labels.Exist(IRobotLabelType.I_LT_BAR_SECTION, name) == 0)
                {
                    _link.CreateRectangularSection(name, b, h, material);
                }
                return name;
            }

            if (_link.IsSectionAvailable(symbol.Name)) return symbol.Name;

            // Section by properties, named after the Revit type
            string label = symbol.Name;
            if (_link.Structure.Labels.Exist(IRobotLabelType.I_LT_BAR_SECTION, label) != 0) return label;

            const double m = 0.3048; // ft -> m
            double[] p = null; // A, Iy, Iz, Ix, Wy, Wz in SI
            if (section is StructuralSectionRound round && round.Diameter > 0)
            {
                double d = round.Diameter * m;
                double i = Math.PI * Math.Pow(d, 4) / 64;
                p = new[] { Math.PI * d * d / 4, i, i, 2 * i, Math.PI * Math.Pow(d, 3) / 32, Math.PI * Math.Pow(d, 3) / 32 };
            }
            else if (section != null && section.SectionArea > 0 && section.MomentOfInertiaStrongAxis > 0)
            {
                p = new[]
                {
                    section.SectionArea * m * m,
                    section.MomentOfInertiaStrongAxis * Math.Pow(m, 4),
                    section.MomentOfInertiaWeakAxis * Math.Pow(m, 4),
                    section.TorsionalMomentOfInertia * Math.Pow(m, 4),
                    section.ElasticModulusStrongAxis * Math.Pow(m, 3),
                    section.ElasticModulusWeakAxis * Math.Pow(m, 3)
                };
            }
            else if (instance != null)
            {
                // families without section properties (common in custom steel families)
                p = SectionFromGeometry(instance);
            }

            if (p == null || p[0] <= 0) return defaultSection;
            return _link.CreateSectionByProperties(label, p[0], p[1], p[2], p[3], p[4], p[5], material);
        }

        /// <summary>
        /// Section properties (A, Iy, Iz, Ix, Wy, Wz in SI) from the end face of a straight member, in the member axes:
        /// z along the section height (SectionUp) and y = z x axis.
        /// The torsion constant uses the Saint-Venant approximation A^4 / (4 pi^2 Ip).
        /// </summary>
        internal static double[] SectionFromGeometry(FamilyInstance fi)
        {
            XYZ p0, p1;
            if (fi.Location is LocationCurve lc && lc.Curve is Line line) { p0 = line.GetEndPoint(0); p1 = line.GetEndPoint(1); }
            else if (fi.Location is LocationPoint lp) { p0 = lp.Point; p1 = lp.Point + XYZ.BasisZ; }
            else return null;

            // same section axes as the bar orientation (SectionUp), so the properties are not rotated twice
            XYZ ex = (p1 - p0).Normalize();
            XYZ ez = SectionUp(fi, ex) ?? (Math.Abs(ex.Z) > 0.999 ? XYZ.BasisX : (XYZ.BasisZ - ex * ex.Z).Normalize());
            XYZ ey = ez.CrossProduct(ex);

            // largest planar face perpendicular to the axis
            PlanarFace face = null;
            foreach (Solid solid in Solids(fi.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })))
            {
                foreach (Face f in solid.Faces)
                {
                    if (f is PlanarFace pf && Math.Abs(pf.FaceNormal.DotProduct(ex)) > 0.999 && (face == null || pf.Area > face.Area)) face = pf;
                }
            }
            if (face == null) return null;

            const double m = 0.3048;
            double a = 0, sy = 0, sz = 0, syy = 0, szz = 0;
            var all = new List<(double Y, double Z)>();
            foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
            {
                var pts = new List<(double Y, double Z)>();
                foreach (Curve c in loop)
                {
                    IList<XYZ> t = c.Tessellate();
                    for (int i = 0; i < t.Count - 1; i++) pts.Add((t[i].DotProduct(ey) * m, t[i].DotProduct(ez) * m));
                }
                // Green's theorem over each loop; holes have the opposite orientation and subtract
                for (int i = 0; i < pts.Count; i++)
                {
                    var (y0, z0) = pts[i];
                    var (y1, z1) = pts[(i + 1) % pts.Count];
                    double cr = y0 * z1 - y1 * z0;
                    a += cr / 2;
                    sy += (y0 + y1) * cr / 6;
                    sz += (z0 + z1) * cr / 6;
                    syy += (y0 * y0 + y0 * y1 + y1 * y1) * cr / 12;
                    szz += (z0 * z0 + z0 * z1 + z1 * z1) * cr / 12;
                }
                all.AddRange(pts);
            }
            if (a < 0) { a = -a; sy = -sy; sz = -sz; syy = -syy; szz = -szz; }
            if (a <= 0) return null;

            double cy = sy / a, cz = sz / a;
            double iy = szz - a * cz * cz; // about y: integral of z^2
            double iz = syy - a * cy * cy; // about z: integral of y^2
            double zmax = all.Max(q => Math.Abs(q.Z - cz)), ymax = all.Max(q => Math.Abs(q.Y - cy));
            double ix = Math.Pow(a, 4) / (4 * Math.PI * Math.PI * (iy + iz));
            return new[] { a, iy, iz, ix, zmax > 0 ? iy / zmax : 0, ymax > 0 ? iz / ymax : 0 };
        }

        private static IEnumerable<Solid> Solids(GeometryElement geometry)
        {
            if (geometry == null) yield break;
            foreach (GeometryObject g in geometry)
            {
                if (g is Solid s && s.Volume > 0) yield return s;
                else if (g is GeometryInstance gi)
                {
                    foreach (Solid inner in Solids(gi.GetInstanceGeometry())) yield return inner;
                }
            }
        }
    }
}
