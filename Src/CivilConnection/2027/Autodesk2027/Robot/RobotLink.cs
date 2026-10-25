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

using RobotOM;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace CivilConnection.Robot
{
    /// <summary>
    /// COM layer for Autodesk Robot Structural Analysis. All values are SI (m, N, Pa, N/m3).
    /// It has no dependency on Revit or Dynamo so it can be exercised outside the host.
    /// </summary>
    internal class RobotLink
    {
        private const double NodeTolerance = 0.001; // m

        private readonly Dictionary<(long, long, long), int> _nodes = new Dictionary<(long, long, long), int>();

        internal IRobotApplication App { get; }

        internal IRobotStructure Structure { get { return App.Project.Structure; } }

        private RobotLink(IRobotApplication app)
        {
            App = app;
        }

        /// <summary>
        /// Connects to the running Robot session. Robot does not register in the ROT,
        /// so creating the COM object attaches to the open instance.
        /// </summary>
        internal static RobotLink Connect(bool newProject, IRobotProjectType projectType)
        {
            if (Process.GetProcessesByName("robot").Length == 0)
            {
                throw new InvalidOperationException("Robot Structural Analysis is not running. Open Robot with a project (any type) and run again.");
            }

            IRobotApplication app = new RobotApplication();

            // A modal dialog raised by the API blocks the COM call indefinitely
            app.Interactive = 0;

            if (newProject || app.Project.IsActive == 0)
            {
                app.Project.New(projectType);
            }

            var link = new RobotLink(app);
            link.LoadExistingNodes();
            return link;
        }

        internal void EndSession()
        {
            App.Interactive = 1;
        }

        private static (long, long, long) Key(double x, double y, double z)
        {
            return ((long)Math.Round(x / NodeTolerance), (long)Math.Round(y / NodeTolerance), (long)Math.Round(z / NodeTolerance));
        }

        private void LoadExistingNodes()
        {
            _nodes.Clear();
            IRobotCollection all = Structure.Nodes.GetAll();
            for (int i = 1; i <= all.Count; i++)
            {
                var n = (IRobotNode)all.Get(i);
                _nodes[Key(n.X, n.Y, n.Z)] = n.Number;
            }
        }

        #region Labels

        internal string CreateMaterial(string name, IRobotMaterialType type, double e, double nu, double unitWeight)
        {
            IRobotLabel label = Structure.Labels.Create(IRobotLabelType.I_LT_MATERIAL, name);
            var data = (IRobotMaterialData)label.Data;
            data.Type = type;
            data.E = e;
            data.NU = nu;
            data.RO = unitWeight;
            data.Kirchoff = e / (2 * (1 + nu));
            Structure.Labels.Store(label);
            return name;
        }

        internal string CreateRectangularSection(string name, double b, double h, string materialName)
        {
            IRobotLabel label = Structure.Labels.Create(IRobotLabelType.I_LT_BAR_SECTION, name);
            var data = (IRobotBarSectionData)label.Data;
            data.Type = IRobotBarSectionType.I_BST_NS_RECT;
            data.ShapeType = IRobotBarSectionShapeType.I_BSST_RECT_FILLED;
            if (!string.IsNullOrEmpty(materialName)) data.MaterialName = materialName;
            var nonstd = data.CreateNonstd(0);
            nonstd.SetValue(IRobotBarSectionNonstdDataValue.I_BSNDV_RECT_B, b);
            nonstd.SetValue(IRobotBarSectionNonstdDataValue.I_BSNDV_RECT_H, h);
            data.CalcNonstdGeometry();
            Structure.Labels.Store(label);
            return name;
        }

        /// <summary>
        /// Uses a section from the Robot database when it exists (e.g. "W 310x52", "HEA 200").
        /// </summary>
        internal bool IsSectionAvailable(string name)
        {
            return Structure.Labels.Exist(IRobotLabelType.I_LT_BAR_SECTION, name) != 0
                || Structure.Labels.IsAvailable(IRobotLabelType.I_LT_BAR_SECTION, name);
        }

        internal string CreatePanelThickness(string name, double thickness, string materialName, double elasticFoundation)
        {
            IRobotLabel label = Structure.Labels.Create(IRobotLabelType.I_LT_PANEL_THICKNESS, name);
            var data = (IRobotThicknessData)label.Data;
            if (!string.IsNullOrEmpty(materialName)) data.MaterialName = materialName;
            data.ThicknessType = IRobotThicknessType.I_TT_HOMOGENEOUS;
            ((IRobotThicknessHomoData)data.Data).ThickConst = thickness;
            if (elasticFoundation > 0) data.ElasticFoundation = elasticFoundation;
            Structure.Labels.Store(label);
            return name;
        }

        internal string CreateSupport(string name, bool ux, bool uy, bool uz, bool rx, bool ry, bool rz)
        {
            IRobotLabel label = Structure.Labels.Create(IRobotLabelType.I_LT_SUPPORT, name);
            var data = (IRobotNodeSupportData)label.Data;
            data.UX = ux ? 1 : 0;
            data.UY = uy ? 1 : 0;
            data.UZ = uz ? 1 : 0;
            data.RX = rx ? 1 : 0;
            data.RY = ry ? 1 : 0;
            data.RZ = rz ? 1 : 0;
            Structure.Labels.Store(label);
            return name;
        }

        #endregion

        #region Geometry

        /// <summary>
        /// Returns the node at the given coordinates, creating it when there is none within 1 mm.
        /// </summary>
        internal int GetOrCreateNode(double x, double y, double z)
        {
            var key = Key(x, y, z);
            if (_nodes.TryGetValue(key, out int number)) return number;

            number = Structure.Nodes.FreeNumber;
            Structure.Nodes.Create(number, x, y, z);
            _nodes[key] = number;
            return number;
        }

        /// <summary>
        /// Returns the existing node closest to the given coordinates (including mesh nodes), without creating one.
        /// </summary>
        internal int FindNearestNode(double x, double y, double z, out double distance)
        {
            int best = 0;
            distance = double.MaxValue;
            IRobotCollection all = Structure.Nodes.GetAll();
            for (int i = 1; i <= all.Count; i++)
            {
                var n = (IRobotNode)all.Get(i);
                double d = Math.Sqrt((n.X - x) * (n.X - x) + (n.Y - y) * (n.Y - y) + (n.Z - z) * (n.Z - z));
                if (d < distance)
                {
                    distance = d;
                    best = n.Number;
                }
            }
            return best;
        }

        internal int CreateBar(double[] start, double[] end, string sectionName, string materialName)
        {
            int n1 = GetOrCreateNode(start[0], start[1], start[2]);
            int n2 = GetOrCreateNode(end[0], end[1], end[2]);
            if (n1 == n2) return 0;

            int number = Structure.Bars.FreeNumber;
            Structure.Bars.Create(number, n1, n2);
            var bar = (IRobotBar)Structure.Bars.Get(number);
            if (!string.IsNullOrEmpty(sectionName)) bar.SetLabel(IRobotLabelType.I_LT_BAR_SECTION, sectionName);
            if (!string.IsNullOrEmpty(materialName)) bar.SetLabel(IRobotLabelType.I_LT_MATERIAL, materialName);
            return number;
        }

        /// <summary>
        /// Creates a meshed panel from a planar closed contour (the last point must not repeat the first).
        /// </summary>
        internal int CreatePanel(IList<double[]> contour, string thicknessName, double meshSize)
        {
            if (contour.Count < 3) throw new ArgumentException("A panel contour needs at least 3 points.");

            var points = new RobotPointsArray();
            points.SetSize(contour.Count);
            for (int i = 0; i < contour.Count; i++)
            {
                points.Set(i + 1, contour[i][0], contour[i][1], contour[i][2]);
            }

            int number = Structure.Objects.FreeNumber;
            Structure.Objects.CreateContour(number, points);
            var panel = (IRobotObjObject)Structure.Objects.Get(number);
            panel.Main.Attribs.Meshed = 1;
            if (!string.IsNullOrEmpty(thicknessName)) panel.SetLabel(IRobotLabelType.I_LT_PANEL_THICKNESS, thicknessName);
            if (meshSize > 0)
            {
                panel.Mesh.Params.MeshType = IRobotMeshType.I_MT_USER;
                panel.Mesh.Params.SurfaceParams.Generation.Type = IRobotMeshGenerationType.I_MGT_ELEMENT_SIZE;
                panel.Mesh.Params.SurfaceParams.Generation.ElementSize = meshSize;
            }
            panel.Initialize();
            panel.Update();
            return number;
        }

        internal void AssignSupport(IEnumerable<int> nodes, string supportName)
        {
            RobotSelection sel = Structure.Selections.Create(IRobotObjectType.I_OT_NODE);
            sel.FromText(ToText(nodes));
            Structure.Nodes.SetLabel(sel, IRobotLabelType.I_LT_SUPPORT, supportName);
        }

        #endregion

        #region Loads

        /// <summary>
        /// Deletes every load case and combination, including the DL1 self-weight case Robot creates by default.
        /// </summary>
        internal void DeleteAllCases()
        {
            RobotCaseCollection all = Structure.Cases.GetAll();
            var numbers = new List<int>();
            for (int i = 1; i <= all.Count; i++) numbers.Add(((IRobotCase)all.Get(i)).Number);
            foreach (int n in numbers) Structure.Cases.Delete(n);
        }

        internal int CreateLoadCase(int number, string name, IRobotCaseNature nature)
        {
            if (number <= 0) number = Structure.Cases.FreeNumber;
            if (Structure.Cases.Exist(number) != 0) Structure.Cases.Delete(number);
            Structure.Cases.CreateSimple(number, name, nature, IRobotCaseAnalizeType.I_CAT_STATIC_LINEAR);
            return number;
        }

        private IRobotLoadRecord NewRecord(int caseNumber, IRobotLoadRecordType type)
        {
            var simple = Structure.Cases.Get(caseNumber) as IRobotSimpleCase
                ?? throw new ArgumentException(string.Format("Load case {0} does not exist or is a combination.", caseNumber));
            int index = simple.Records.New(type);
            return simple.Records.Get(index);
        }

        internal void AddSelfWeight(int caseNumber, double factor)
        {
            IRobotLoadRecord rec = NewRecord(caseNumber, IRobotLoadRecordType.I_LRT_DEAD);
            rec.SetValue((short)IRobotDeadRecordValues.I_DRV_Z, -1);
            rec.SetValue((short)IRobotDeadRecordValues.I_DRV_COEFF, factor);
            rec.SetValue((short)IRobotDeadRecordValues.I_DRV_ENTIRE_STRUCTURE, 1);
        }

        internal void AddNodalForce(int caseNumber, IEnumerable<int> nodes, double fx, double fy, double fz, double mx, double my, double mz)
        {
            IRobotLoadRecord rec = NewRecord(caseNumber, IRobotLoadRecordType.I_LRT_NODE_FORCE);
            rec.SetValue((short)IRobotNodeForceRecordValues.I_NFRV_FX, fx);
            rec.SetValue((short)IRobotNodeForceRecordValues.I_NFRV_FY, fy);
            rec.SetValue((short)IRobotNodeForceRecordValues.I_NFRV_FZ, fz);
            rec.SetValue((short)IRobotNodeForceRecordValues.I_NFRV_CX, mx);
            rec.SetValue((short)IRobotNodeForceRecordValues.I_NFRV_CY, my);
            rec.SetValue((short)IRobotNodeForceRecordValues.I_NFRV_CZ, mz);
            rec.Objects.FromText(ToText(nodes));
        }

        internal void AddBarUniformLoad(int caseNumber, IEnumerable<int> bars, double px, double py, double pz, bool local)
        {
            IRobotLoadRecord rec = NewRecord(caseNumber, IRobotLoadRecordType.I_LRT_BAR_UNIFORM);
            rec.SetValue((short)IRobotBarUniformRecordValues.I_BURV_PX, px);
            rec.SetValue((short)IRobotBarUniformRecordValues.I_BURV_PY, py);
            rec.SetValue((short)IRobotBarUniformRecordValues.I_BURV_PZ, pz);
            rec.SetValue((short)IRobotBarUniformRecordValues.I_BURV_LOCAL, local ? 1 : 0);
            rec.Objects.FromText(ToText(bars));
        }

        internal void AddPanelUniformLoad(int caseNumber, IEnumerable<int> panels, double px, double py, double pz, bool local)
        {
            IRobotLoadRecord rec = NewRecord(caseNumber, IRobotLoadRecordType.I_LRT_UNIFORM);
            rec.SetValue((short)IRobotUniformRecordValues.I_URV_PX, px);
            rec.SetValue((short)IRobotUniformRecordValues.I_URV_PY, py);
            rec.SetValue((short)IRobotUniformRecordValues.I_URV_PZ, pz);
            rec.SetValue((short)IRobotUniformRecordValues.I_URV_LOCAL_SYSTEM, local ? 1 : 0);
            rec.Objects.FromText(ToText(panels));
        }

        internal int CreateCombination(int number, string name, IRobotCombinationType type, IList<int> cases, IList<double> factors)
        {
            if (cases.Count != factors.Count) throw new ArgumentException("Each load case needs a factor.");
            if (number <= 0) number = Structure.Cases.FreeNumber;
            if (Structure.Cases.Exist(number) != 0) Structure.Cases.Delete(number);
            RobotCaseCombination comb = Structure.Cases.CreateCombination(number, name, type, IRobotCaseNature.I_CN_PERMANENT, IRobotCaseAnalizeType.I_CAT_COMB);
            for (int i = 0; i < cases.Count; i++) comb.CaseFactors.New(cases[i], factors[i]);
            return number;
        }

        #endregion

        #region Analysis and results

        internal void GenerateMesh()
        {
            Structure.Objects.Mesh.Generate();

            // the mesh adds nodes; reuse them instead of creating coincident ones
            LoadExistingNodes();
        }

        /// <summary>
        /// Runs the analysis. Returns the Robot status code (0 = success).
        /// </summary>
        internal int Calculate()
        {
            RobotCalcEngine engine = App.Project.CalcEngine;
            engine.AnalysisParams.IgnoreWarnings = true;
            engine.UseStatusWindow = false;
            return engine.Calculate();
        }

        internal bool ResultsAvailable
        {
            get { return Structure.Results.Available != 0; }
        }

        private void EnsureResults()
        {
            if (!ResultsAvailable) throw new InvalidOperationException("The Robot model has no results. Run the calculation (Analysis > Calculations) first.");
        }

        internal double[] NodeDisplacement(int node, int caseNumber)
        {
            EnsureResults();
            IRobotDisplacementData d = Structure.Results.Nodes.Displacements.Value(node, caseNumber);
            return new[] { d.UX, d.UY, d.UZ, d.RX, d.RY, d.RZ };
        }

        internal double[] NodeReaction(int node, int caseNumber)
        {
            EnsureResults();
            IRobotReactionData r = Structure.Results.Nodes.Reactions.Value(node, caseNumber);
            return new[] { r.FX, r.FY, r.FZ, r.MX, r.MY, r.MZ };
        }

        /// <summary>
        /// Bar internal forces at a relative position (0 = start, 1 = end).
        /// </summary>
        internal double[] BarForce(int bar, int caseNumber, double position)
        {
            EnsureResults();
            RobotBarForceData f = Structure.Results.Bars.Forces.Value(bar, caseNumber, position);
            return new[] { f.FX, f.FY, f.FZ, f.MX, f.MY, f.MZ };
        }

        /// <summary>
        /// Detailed shell forces (MXX, MYY, MXY, NXX, NYY, NXY, QXX, QYY) averaged at the panel's finite elements.
        /// </summary>
        internal List<double[]> PanelElementForces(int panel, int caseNumber, out List<int> elements)
        {
            EnsureResults();
            elements = new List<int>();
            var output = new List<double[]>();

            var obj = (IRobotObjObject)Structure.Objects.Get(panel);
            string feText = obj.FiniteElems;
            if (string.IsNullOrWhiteSpace(feText)) return output;

            RobotSelection sel = Structure.Selections.Create(IRobotObjectType.I_OT_FINITE_ELEMENT);
            sel.FromText(feText);

            var param = (RobotFeResultParams)App.CmpntFactory.Create(IRobotComponentType.I_CT_FE_RESULT_PARAMS);
            param.Case = caseNumber;
            param.Panel = panel;
            param.Node = 0;
            param.Layer = IRobotFeLayerType.I_FLT_MIDDLE;

            for (int i = 1; i <= sel.Count; i++)
            {
                int element = sel.Get(i);
                param.Element = element;
                RobotFeResultDetailed d = Structure.Results.FiniteElems.Detailed(param);
                elements.Add(element);
                output.Add(new[] { d.MXX, d.MYY, d.MXY, d.NXX, d.NYY, d.NXY, d.QXX, d.QYY });
            }
            return output;
        }

        #endregion

        internal static string ToText(IEnumerable<int> numbers)
        {
            return string.Join(" ", numbers.Where(n => n > 0).Select(n => n.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
