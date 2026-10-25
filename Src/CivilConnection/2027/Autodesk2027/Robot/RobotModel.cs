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

using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Structure.StructuralSections;
using RobotOM;
using RevitServices.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using DB = Autodesk.Revit.DB;

namespace CivilConnection.Robot
{
    /// <summary>
    /// Connection to a running Autodesk Robot Structural Analysis session.
    /// Geometry is converted with LengthUnitToMeters and shifted by the Origin; loads and results are SI (N, m, Pa).
    /// Every run of the graph adds objects to the Robot model: use Connect with newProject = true to start over.
    /// </summary>
    public class RobotModel
    {
        internal RobotLink Link { get; }

        /// <summary>
        /// Factor that converts one Dynamo length unit to meters.
        /// </summary>
        public double LengthUnitToMeters { get; }

        /// <summary>
        /// Point of the input geometry that becomes the Robot origin (0,0,0).
        /// </summary>
        public Point Origin { get; }

        private RobotModel(RobotLink link, double lengthUnitToMeters, Point origin)
        {
            Link = link;
            LengthUnitToMeters = lengthUnitToMeters;
            Origin = origin;
        }

        /// <summary>
        /// Connects to the open Robot session. Robot must be running with a project open.
        /// </summary>
        /// <param name="newProject">Start a new Robot project, discarding the current one.</param>
        /// <param name="structureType">Frame3D, Truss3D, Grillage, Plate or Shell.</param>
        /// <param name="lengthUnitToMeters">Meters per Dynamo length unit. Use a negative value to read it from the Revit document units.</param>
        /// <param name="origin">Point of the input geometry placed at the Robot origin, e.g. a point near the Civil 3D model to avoid large coordinates.</param>
        /// <returns></returns>
        public static RobotModel Connect(bool newProject = false, string structureType = "Shell", double lengthUnitToMeters = -1, [DefaultArgument("null")] Point origin = null)
        {
            if (!Enum.TryParse("I_PT_" + structureType.ToUpperInvariant().Replace("FRAME3D", "FRAME_3D").Replace("TRUSS3D", "TRUSS_3D"), out IRobotProjectType type))
            {
                throw new ArgumentException("structureType must be Frame3D, Truss3D, Grillage, Plate or Shell.");
            }

            if (lengthUnitToMeters <= 0)
            {
                lengthUnitToMeters = RevitLengthUnitToMeters();
            }

            return new RobotModel(RobotLink.Connect(newProject, type), lengthUnitToMeters, origin);
        }

        /// <summary>
        /// Meters per Dynamo length unit for the active Revit document.
        /// </summary>
        /// <returns></returns>
        public static double RevitLengthUnitToMeters()
        {
            // Dynamo -> Revit internal feet -> meters
            return Revit.GeometryConversion.UnitConverter.DynamoToHostFactor(DB.SpecTypeId.Length) * 0.3048;
        }

        /// <summary>
        /// Robot version.
        /// </summary>
        public double Version { get { return Link.App.Version; } }

        /// <summary>
        /// Number of nodes in the Robot model.
        /// </summary>
        public int NodeCount { get { return Link.Structure.Nodes.GetAll().Count; } }

        /// <summary>
        /// Number of bars in the Robot model.
        /// </summary>
        public int BarCount { get { return Link.Structure.Bars.GetAll().Count; } }

        /// <summary>
        /// True when the Robot model has calculation results.
        /// </summary>
        public bool ResultsAvailable { get { return Link.ResultsAvailable; } }

        #region Geometry conversion

        private double[] ToRobot(Point p)
        {
            double ox = Origin?.X ?? 0, oy = Origin?.Y ?? 0, oz = Origin?.Z ?? 0;
            return new[] { (p.X - ox) * LengthUnitToMeters, (p.Y - oy) * LengthUnitToMeters, (p.Z - oz) * LengthUnitToMeters };
        }

        /// <summary>
        /// Splits a curve in straight segments no longer than maxSegmentLength (model units). Lines are kept whole.
        /// </summary>
        private static IList<Point> Divide(Curve curve, double maxSegmentLength)
        {
            if (curve is Line || maxSegmentLength <= 0)
            {
                return new List<Point> { curve.StartPoint, curve.EndPoint };
            }

            int n = Math.Max(1, (int)Math.Ceiling(curve.Length / maxSegmentLength));
            return Enumerable.Range(0, n + 1).Select(i => curve.PointAtParameter((double)i / n)).ToList();
        }

        /// <summary>
        /// Ordered contour points of a closed outline, curved edges divided by maxSegmentLength.
        /// </summary>
        private IList<double[]> Contour(object outline, double maxSegmentLength)
        {
            IList<Curve> curves;
            if (outline is Surface surface) curves = surface.PerimeterCurves();
            else if (outline is PolyCurve pc) curves = pc.Curves();
            else if (outline is Polygon pg) curves = pg.Curves();
            else throw new ArgumentException("Panel outlines must be Surfaces, Polygons or closed PolyCurves.");

            var points = new List<Point>();
            var remaining = curves.ToList();
            Point current = remaining[0].StartPoint;
            while (remaining.Count > 0)
            {
                // next edge connected to the current point, in either direction
                Curve next = remaining.OrderBy(c => Math.Min(c.StartPoint.DistanceTo(current), c.EndPoint.DistanceTo(current))).First();
                remaining.Remove(next);
                IList<Point> pts = Divide(next, maxSegmentLength);
                if (pts[0].DistanceTo(current) > pts[pts.Count - 1].DistanceTo(current)) pts = pts.Reverse().ToList();
                points.AddRange(pts.Take(pts.Count - 1));
                current = pts[pts.Count - 1];
            }
            return points.Select(ToRobot).ToList();
        }

        #endregion

        #region Labels

        /// <summary>
        /// Creates an isotropic material.
        /// </summary>
        /// <param name="name">Material name.</param>
        /// <param name="youngModulus">E in Pa (e.g. 26.8e9 for C30 per NBR 6118 with granite aggregate).</param>
        /// <param name="poisson">Poisson's ratio.</param>
        /// <param name="unitWeight">Unit weight in N/m3 (e.g. 25000 for reinforced concrete).</param>
        /// <param name="materialType">Concrete, Steel, Timber, Aluminium or Other.</param>
        /// <returns>The material name.</returns>
        public string CreateMaterial(string name, double youngModulus, double poisson = 0.2, double unitWeight = 25000, string materialType = "Concrete")
        {
            if (!Enum.TryParse("I_MT_" + materialType.ToUpperInvariant(), out IRobotMaterialType type))
            {
                throw new ArgumentException("materialType must be Concrete, Steel, Timber, Aluminium or Other.");
            }
            return Link.CreateMaterial(name, type, youngModulus, poisson, unitWeight);
        }

        /// <summary>
        /// Creates a solid rectangular section. Dimensions in meters.
        /// </summary>
        /// <returns>The section name.</returns>
        public string CreateRectangularSection(string name, double width, double height, string materialName = "")
        {
            return Link.CreateRectangularSection(name, width, height, materialName);
        }

        /// <summary>
        /// Creates a panel thickness, optionally on an elastic (Winkler) foundation.
        /// </summary>
        /// <param name="name">Thickness name.</param>
        /// <param name="thickness">Thickness in meters.</param>
        /// <param name="materialName">Material name.</param>
        /// <param name="elasticFoundation">Subgrade reaction modulus kv in N/m3 (0 = none).</param>
        /// <returns>The thickness name.</returns>
        public string CreatePanelThickness(string name, double thickness, string materialName = "", double elasticFoundation = 0)
        {
            return Link.CreatePanelThickness(name, thickness, materialName, elasticFoundation);
        }

        /// <summary>
        /// Creates a nodal support. True blocks the degree of freedom.
        /// </summary>
        /// <returns>The support name.</returns>
        public string CreateSupport(string name, bool ux = true, bool uy = true, bool uz = true, bool rx = false, bool ry = false, bool rz = false)
        {
            return Link.CreateSupport(name, ux, uy, uz, rx, ry, rz);
        }

        #endregion

        #region Geometry

        /// <summary>
        /// Creates Robot nodes, reusing existing nodes within 1 mm.
        /// </summary>
        /// <returns>Node numbers.</returns>
        public IList<int> CreateNodes(IList<Point> points)
        {
            return points.Select(p => { var c = ToRobot(p); return Link.GetOrCreateNode(c[0], c[1], c[2]); }).ToList();
        }

        /// <summary>
        /// Returns the existing Robot nodes closest to the points (including panel mesh nodes), without creating nodes.
        /// Use it to read results at given locations of a panel.
        /// </summary>
        /// <returns>Node numbers and their distance to each point in meters.</returns>
        [MultiReturn(new[] { "Nodes", "Distances" })]
        public Dictionary<string, object> NearestNodes(IList<Point> points, [DefaultArgument("null")] object waitFor = null)
        {
            var nodes = new List<int>();
            var distances = new List<double>();
            foreach (Point p in points)
            {
                var c = ToRobot(p);
                nodes.Add(Link.FindNearestNode(c[0], c[1], c[2], out double d));
                distances.Add(d);
            }
            return new Dictionary<string, object> { { "Nodes", nodes }, { "Distances", distances } };
        }

        /// <summary>
        /// Creates bars along curves. Curved elements are divided in straight bars no longer than maxSegmentLength (model units).
        /// </summary>
        /// <returns>Bar numbers for each curve.</returns>
        public IList<IList<int>> CreateBars(IList<Curve> curves, string sectionName, string materialName = "", double maxSegmentLength = 1)
        {
            var output = new List<IList<int>>();
            foreach (Curve curve in curves)
            {
                IList<Point> pts = Divide(curve, maxSegmentLength);
                var bars = new List<int>();
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    int bar = Link.CreateBar(ToRobot(pts[i]), ToRobot(pts[i + 1]), sectionName, materialName);
                    if (bar > 0) bars.Add(bar);
                }
                output.Add(bars);
            }
            return output;
        }

        /// <summary>
        /// Creates meshed panels from planar Surfaces, Polygons or closed PolyCurves.
        /// </summary>
        /// <param name="outlines">Planar closed outlines.</param>
        /// <param name="thicknessName">Panel thickness name.</param>
        /// <param name="meshSize">Finite element size in meters (0 = Robot default).</param>
        /// <param name="maxSegmentLength">Division of curved edges, in model units.</param>
        /// <returns>Panel numbers.</returns>
        public IList<int> CreatePanels(IList<object> outlines, string thicknessName, double meshSize = 0, double maxSegmentLength = 1)
        {
            return outlines.Select(o => Link.CreatePanel(Contour(o, maxSegmentLength), thicknessName, meshSize)).ToList();
        }

        /// <summary>
        /// Assigns a support label to nodes.
        /// </summary>
        /// <returns>The node numbers.</returns>
        public IList<int> AssignSupport(IList<int> nodeNumbers, string supportName)
        {
            Link.AssignSupport(nodeNumbers, supportName);
            return nodeNumbers;
        }

        /// <summary>
        /// Assigns a support label to the nodes closest to the given points.
        /// </summary>
        /// <returns>The node numbers.</returns>
        public IList<int> AssignSupportAtPoints(IList<Point> points, string supportName)
        {
            return AssignSupport(CreateNodes(points), supportName);
        }

        #endregion

        #region Civil 3D

        /// <summary>
        /// Creates bars along Civil 3D corridor feature lines (e.g. girders or kerbs).
        /// </summary>
        /// <returns>Bar numbers for each feature line.</returns>
        public IList<IList<int>> BarsFromFeaturelines(IList<Featureline> featurelines, string sectionName, string materialName = "", double maxSegmentLength = 1)
        {
            return CreateBars(featurelines.Select(f => (Curve)f.Curve).ToList(), sectionName, materialName, maxSegmentLength);
        }

        /// <summary>
        /// Creates bars along a Civil 3D alignment.
        /// </summary>
        /// <returns>Bar numbers for each alignment entity.</returns>
        public IList<IList<int>> BarsFromAlignment(Alignment alignment, string sectionName, string materialName = "", double maxSegmentLength = 1)
        {
            return CreateBars(alignment.GetCurves(maxSegmentLength), sectionName, materialName, maxSegmentLength);
        }

        /// <summary>
        /// Creates deck panels between two corridor feature lines (e.g. the edges of a bridge deck),
        /// split every stationStep along the first feature line. Non planar strips are split in two triangles.
        /// </summary>
        /// <param name="left">First edge feature line.</param>
        /// <param name="right">Second edge feature line.</param>
        /// <param name="thicknessName">Panel thickness name.</param>
        /// <param name="stationStep">Panel length along the feature line, in model units.</param>
        /// <param name="meshSize">Finite element size in meters (0 = Robot default).</param>
        /// <returns>Panel numbers.</returns>
        public IList<int> DeckPanelsBetweenFeaturelines(Featureline left, Featureline right, string thicknessName, double stationStep = 5, double meshSize = 0)
        {
            var output = new List<int>();
            Curve a = left.Curve;
            Curve b = right.Curve;
            int n = Math.Max(1, (int)Math.Ceiling(a.Length / stationStep));

            Point Closest(Curve c, Point p) => c.ClosestPointTo(p);

            for (int i = 0; i < n; i++)
            {
                Point a0 = a.PointAtParameter((double)i / n);
                Point a1 = a.PointAtParameter((double)(i + 1) / n);
                Point b0 = Closest(b, a0);
                Point b1 = Closest(b, a1);

                var quad = new List<double[]> { ToRobot(a0), ToRobot(a1), ToRobot(b1), ToRobot(b0) };
                if (IsPlanar(quad, 0.001))
                {
                    output.Add(Link.CreatePanel(quad, thicknessName, meshSize));
                }
                else
                {
                    output.Add(Link.CreatePanel(new List<double[]> { quad[0], quad[1], quad[2] }, thicknessName, meshSize));
                    output.Add(Link.CreatePanel(new List<double[]> { quad[0], quad[2], quad[3] }, thicknessName, meshSize));
                }
            }
            return output;
        }

        private static bool IsPlanar(IList<double[]> p, double tolerance)
        {
            double[] u = { p[1][0] - p[0][0], p[1][1] - p[0][1], p[1][2] - p[0][2] };
            double[] v = { p[3][0] - p[0][0], p[3][1] - p[0][1], p[3][2] - p[0][2] };
            double[] w = { p[2][0] - p[0][0], p[2][1] - p[0][1], p[2][2] - p[0][2] };
            double[] nrm = { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
            double len = Math.Sqrt(nrm[0] * nrm[0] + nrm[1] * nrm[1] + nrm[2] * nrm[2]);
            if (len < 1e-12) return false;
            return Math.Abs((nrm[0] * w[0] + nrm[1] * w[1] + nrm[2] * w[2]) / len) <= tolerance;
        }

        #endregion

        #region Revit

        /// <summary>
        /// Sends Revit structural elements to Robot through their analytical model.
        /// Accepts physical elements (beams, columns, braces, floors, walls) or analytical members and panels.
        /// Rectangular sections and materials with structural assets are created in Robot; other sections
        /// are looked up by family type name in the Robot section database.
        /// </summary>
        /// <param name="elements">Revit elements.</param>
        /// <param name="defaultSectionName">Section used when the Revit type has no match in Robot.</param>
        /// <param name="maxSegmentLength">Division of curved members, in model units.</param>
        /// <param name="meshSize">Finite element size of panels in meters (0 = Robot default).</param>
        /// <returns>Revit element ids with the Robot bar and panel numbers created for each.</returns>
        [MultiReturn(new[] { "ElementIds", "Bars", "Panels", "Skipped" })]
        public Dictionary<string, object> FromRevitAnalytical(IList<Revit.Elements.Element> elements, string defaultSectionName = "", double maxSegmentLength = 1, double meshSize = 0)
        {
            DB.Document doc = DocumentManager.Instance.CurrentDBDocument;
            var association = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);

            var ids = new List<long>();
            var bars = new List<IList<int>>();
            var panels = new List<int>();
            var skipped = new List<long>();

            foreach (Revit.Elements.Element e in elements)
            {
                DB.Element element = e.InternalElement;
                DB.Element analytical = element as AnalyticalElement;
                if (analytical == null)
                {
                    DB.ElementId aid = association?.GetAssociatedElementId(element.Id);
                    if (aid != null && aid != DB.ElementId.InvalidElementId) analytical = doc.GetElement(aid);
                }

                if (analytical is AnalyticalMember member)
                {
                    string material = MaterialFromRevit(doc, member.MaterialId);
                    string section = SectionFromRevit(doc, member.SectionTypeId, material, defaultSectionName);
                    Curve curve = Revit.GeometryConversion.RevitToProtoCurve.ToProtoType(member.GetCurve(), true, null);
                    ids.Add(element.Id.GetValue());
                    bars.Add(CreateBars(new List<Curve> { curve }, section, material, maxSegmentLength)[0]);
                    panels.Add(0);
                }
                else if (analytical is AnalyticalPanel panel)
                {
                    string material = MaterialFromRevit(doc, panel.MaterialId);
                    double thickness = DB.UnitUtils.ConvertFromInternalUnits(panel.Thickness, DB.UnitTypeId.Meters);
                    string thicknessName = string.Format("TH{0:0}_{1}", thickness * 1000, material);
                    if (Link.Structure.Labels.Exist(IRobotLabelType.I_LT_PANEL_THICKNESS, thicknessName) == 0)
                    {
                        Link.CreatePanelThickness(thicknessName, thickness, material, 0);
                    }

                    var outline = new List<Curve>();
                    foreach (DB.Curve c in panel.GetOuterContour())
                    {
                        outline.Add(Revit.GeometryConversion.RevitToProtoCurve.ToProtoType(c, true, null));
                    }
                    ids.Add(element.Id.GetValue());
                    bars.Add(new List<int>());
                    panels.Add(Link.CreatePanel(Contour(PolyCurve.ByJoinedCurves(outline, 0.001, false, 0), maxSegmentLength), thicknessName, meshSize));
                }
                else
                {
                    skipped.Add(element.Id.GetValue());
                }
            }

            return new Dictionary<string, object>
            {
                { "ElementIds", ids },
                { "Bars", bars },
                { "Panels", panels },
                { "Skipped", skipped }
            };
        }

        private string MaterialFromRevit(DB.Document doc, DB.ElementId materialId)
        {
            var material = doc.GetElement(materialId) as DB.Material;
            if (material == null) return "";

            string name = material.Name;
            if (Link.Structure.Labels.Exist(IRobotLabelType.I_LT_MATERIAL, name) != 0) return name;

            var asset = doc.GetElement(material.StructuralAssetId) as DB.PropertySetElement;
            DB.StructuralAsset sa = asset?.GetStructuralAsset();
            if (sa == null) return "";

            double e = DB.UnitUtils.ConvertFromInternalUnits(sa.YoungModulus.X, DB.UnitTypeId.Pascals);
            double nu = sa.PoissonRatio.X;
            double density = DB.UnitUtils.ConvertFromInternalUnits(sa.Density, DB.UnitTypeId.KilogramsPerCubicMeter);
            IRobotMaterialType type = sa.StructuralAssetClass == DB.StructuralAssetClass.Metal ? IRobotMaterialType.I_MT_STEEL
                : sa.StructuralAssetClass == DB.StructuralAssetClass.Wood ? IRobotMaterialType.I_MT_TIMBER
                : sa.StructuralAssetClass == DB.StructuralAssetClass.Concrete ? IRobotMaterialType.I_MT_CONCRETE
                : IRobotMaterialType.I_MT_OTHER;

            return Link.CreateMaterial(name, type, e, nu, density * 9.80665);
        }

        private string SectionFromRevit(DB.Document doc, DB.ElementId typeId, string material, string defaultSection)
        {
            var symbol = doc.GetElement(typeId) as DB.FamilySymbol;
            if (symbol == null) return defaultSection;

            if (symbol.GetStructuralSection() is StructuralSectionRectangular rect)
            {
                double b = DB.UnitUtils.ConvertFromInternalUnits(rect.Width, DB.UnitTypeId.Meters);
                double h = DB.UnitUtils.ConvertFromInternalUnits(rect.Height, DB.UnitTypeId.Meters);
                string name = string.Format("R{0:0}x{1:0}", b * 1000, h * 1000);
                if (Link.Structure.Labels.Exist(IRobotLabelType.I_LT_BAR_SECTION, name) == 0)
                {
                    Link.CreateRectangularSection(name, b, h, material);
                }
                return name;
            }

            return Link.IsSectionAvailable(symbol.Name) ? symbol.Name : defaultSection;
        }

        #endregion

        #region Loads

        /// <summary>
        /// Deletes all load cases and combinations, including the self-weight case DL1 Robot creates by default.
        /// </summary>
        /// <param name="waitFor">Any input that must be computed first.</param>
        /// <returns>True when done.</returns>
        public bool DeleteAllCases([DefaultArgument("null")] object waitFor = null)
        {
            Link.DeleteAllCases();
            return true;
        }

        /// <summary>
        /// Creates a static linear load case.
        /// </summary>
        /// <param name="number">Case number (0 = next free number). An existing case with this number is replaced.</param>
        /// <param name="name">Case name.</param>
        /// <param name="nature">Permanent, Exploatation (live), Wind, Snow, Temperature, Accidental or Seismic.</param>
        /// <param name="waitFor">Any input that must be computed first.</param>
        /// <returns>The case number.</returns>
        public int CreateLoadCase(int number, string name, string nature = "Permanent", [DefaultArgument("null")] object waitFor = null)
        {
            if (!Enum.TryParse("I_CN_" + nature.ToUpperInvariant(), out IRobotCaseNature caseNature))
            {
                throw new ArgumentException("nature must be Permanent, Exploatation, Wind, Snow, Temperature, Accidental or Seismic.");
            }
            return Link.CreateLoadCase(number, name, caseNature);
        }

        /// <summary>
        /// Adds the self-weight of the whole structure (-Z) to a load case.
        /// </summary>
        /// <returns>The case number.</returns>
        public int AddSelfWeight(int caseNumber, double factor = 1)
        {
            Link.AddSelfWeight(caseNumber, factor);
            return caseNumber;
        }

        /// <summary>
        /// Adds nodal forces (N) and moments (N.m) in global axes.
        /// </summary>
        /// <returns>The case number.</returns>
        public int AddNodalForces(int caseNumber, IList<int> nodeNumbers, double fx = 0, double fy = 0, double fz = 0, double mx = 0, double my = 0, double mz = 0)
        {
            Link.AddNodalForce(caseNumber, nodeNumbers, fx, fy, fz, mx, my, mz);
            return caseNumber;
        }

        /// <summary>
        /// Adds a uniform load (N/m) to bars, in global axes unless local is true.
        /// </summary>
        /// <returns>The case number.</returns>
        public int AddBarUniformLoads(int caseNumber, IList<int> barNumbers, double px = 0, double py = 0, double pz = 0, bool local = false)
        {
            Link.AddBarUniformLoad(caseNumber, barNumbers, px, py, pz, local);
            return caseNumber;
        }

        /// <summary>
        /// Adds a uniform surface load (Pa = N/m2) to panels, in global axes unless local is true.
        /// </summary>
        /// <returns>The case number.</returns>
        public int AddPanelUniformLoads(int caseNumber, IList<int> panelNumbers, double px = 0, double py = 0, double pz = 0, bool local = false)
        {
            Link.AddPanelUniformLoad(caseNumber, panelNumbers, px, py, pz, local);
            return caseNumber;
        }

        /// <summary>
        /// Creates a linear combination of load cases.
        /// </summary>
        /// <param name="number">Combination number (0 = next free number).</param>
        /// <param name="name">Combination name.</param>
        /// <param name="caseNumbers">Load cases.</param>
        /// <param name="factors">Factor of each load case (e.g. 1.4, 1.4).</param>
        /// <param name="combinationType">ULS, SLS or ALS (accidental).</param>
        /// <returns>The combination number.</returns>
        public int CreateCombination(int number, string name, IList<int> caseNumbers, IList<double> factors, string combinationType = "ULS")
        {
            if (!Enum.TryParse("I_CBT_" + combinationType.ToUpperInvariant(), out IRobotCombinationType type))
            {
                throw new ArgumentException("combinationType must be ULS, SLS or ALS.");
            }
            return Link.CreateCombination(number, name, type, caseNumbers, factors);
        }

        #endregion

        #region Analysis

        /// <summary>
        /// Generates the finite element mesh of the panels.
        /// </summary>
        /// <param name="waitFor">Any input that must be computed first.</param>
        /// <returns>True when done.</returns>
        public bool GenerateMesh([DefaultArgument("null")] object waitFor = null)
        {
            Link.GenerateMesh();
            return true;
        }

        /// <summary>
        /// Runs the Robot analysis. Calculate is a long, blocking call; if it does not return on your installation,
        /// run Analysis > Calculations in Robot and read the results with the result nodes.
        /// </summary>
        /// <param name="run">Set to true to run the analysis.</param>
        /// <param name="waitFor">Any input that must be computed first (e.g. the last load or combination).</param>
        /// <returns>True when Robot has results.</returns>
        public bool Calculate(bool run = false, [DefaultArgument("null")] object waitFor = null)
        {
            if (run) Link.Calculate();
            return Link.ResultsAvailable;
        }

        /// <summary>
        /// Saves the Robot model (.rtd).
        /// </summary>
        /// <param name="path">File path.</param>
        /// <param name="waitFor">Any input that must be computed first.</param>
        /// <returns>The file path.</returns>
        public string SaveAs(string path, [DefaultArgument("null")] object waitFor = null)
        {
            Link.App.Project.SaveAs(path);
            return path;
        }

        /// <summary>
        /// Releases the Robot user interface (enables dialogs again).
        /// </summary>
        /// <param name="waitFor">Any input that must be computed first.</param>
        /// <returns>True when done.</returns>
        public bool EndSession([DefaultArgument("null")] object waitFor = null)
        {
            Link.EndSession();
            return true;
        }

        #endregion

        #region Results

        /// <summary>
        /// Node displacements (m) and rotations (rad) for a load case or combination.
        /// </summary>
        [MultiReturn(new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" })]
        public Dictionary<string, object> NodeDisplacements(IList<int> nodeNumbers, int caseNumber, [DefaultArgument("null")] object waitFor = null)
        {
            return Table(new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }, nodeNumbers.Select(n => Link.NodeDisplacement(n, caseNumber)));
        }

        /// <summary>
        /// Support reactions (N, N.m) for a load case or combination.
        /// </summary>
        [MultiReturn(new[] { "FX", "FY", "FZ", "MX", "MY", "MZ" })]
        public Dictionary<string, object> NodeReactions(IList<int> nodeNumbers, int caseNumber, [DefaultArgument("null")] object waitFor = null)
        {
            return Table(new[] { "FX", "FY", "FZ", "MX", "MY", "MZ" }, nodeNumbers.Select(n => Link.NodeReaction(n, caseNumber)));
        }

        /// <summary>
        /// Bar internal forces (N, N.m) at equally spaced points along each bar.
        /// </summary>
        /// <param name="barNumbers">Bars.</param>
        /// <param name="caseNumber">Load case or combination.</param>
        /// <param name="points">Number of points along each bar (2 = ends only).</param>
        /// <param name="waitFor">Any input that must be computed first.</param>
        [MultiReturn(new[] { "FX", "FY", "FZ", "MX", "MY", "MZ" })]
        public Dictionary<string, object> BarForces(IList<int> barNumbers, int caseNumber, int points = 3, [DefaultArgument("null")] object waitFor = null)
        {
            points = Math.Max(2, points);
            var keys = new[] { "FX", "FY", "FZ", "MX", "MY", "MZ" };
            var output = keys.ToDictionary(k => k, k => (object)new List<IList<double>>());
            foreach (int bar in barNumbers)
            {
                var rows = Enumerable.Range(0, points).Select(i => Link.BarForce(bar, caseNumber, (double)i / (points - 1))).ToList();
                for (int k = 0; k < keys.Length; k++) ((List<IList<double>>)output[keys[k]]).Add(rows.Select(r => r[k]).ToList());
            }
            return output;
        }

        /// <summary>
        /// Shell forces per finite element of a panel: moments (N.m/m), membrane forces and shear (N/m).
        /// </summary>
        [MultiReturn(new[] { "Elements", "MXX", "MYY", "MXY", "NXX", "NYY", "NXY", "QXX", "QYY" })]
        public Dictionary<string, object> PanelForces(int panelNumber, int caseNumber, [DefaultArgument("null")] object waitFor = null)
        {
            List<double[]> rows = Link.PanelElementForces(panelNumber, caseNumber, out List<int> elements);
            var output = Table(new[] { "MXX", "MYY", "MXY", "NXX", "NYY", "NXY", "QXX", "QYY" }, rows);
            output["Elements"] = elements;
            return output;
        }

        private static Dictionary<string, object> Table(string[] keys, IEnumerable<double[]> rows)
        {
            List<double[]> list = rows.ToList();
            var output = new Dictionary<string, object>();
            for (int k = 0; k < keys.Length; k++) output[keys[k]] = list.Select(r => r[k]).ToList();
            return output;
        }

        #endregion

        /// <summary>
        /// Returns a string that represents this instance.
        /// </summary>
        public override string ToString()
        {
            return string.Format("RobotModel(Version = {0}, Nodes = {1}, Bars = {2})", Version, NodeCount, BarCount);
        }
    }
}
