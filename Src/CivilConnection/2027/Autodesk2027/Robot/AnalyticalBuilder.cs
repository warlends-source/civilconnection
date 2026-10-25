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
using System;
using System.Collections.Generic;
using System.Linq;

namespace CivilConnection.Robot
{
    /// <summary>
    /// Creates a connected Revit analytical model from the physical structural elements.
    /// Physical location lines rarely meet (beams stop at column faces, purlins sit on rafters), so every member end
    /// is moved to the closest member within a tolerance: first to members of higher priority (columns, then longer
    /// members), then, for ends that are still free, to any member. An end goes to an end point when one is within
    /// the tolerance, otherwise to the closest point of the member axis. Depends on the Revit API only; the caller
    /// manages the transaction.
    /// </summary>
    internal class AnalyticalBuilder
    {
        private const double Coincident = 0.001 / 0.3048; // 1 mm in feet

        private class Member
        {
            public FamilyInstance Element;
            public XYZ A, B;
            public bool IsColumn;
        }

        internal int MembersCreated { get; private set; }
        internal int PanelsCreated { get; private set; }
        internal int EndsMoved { get; private set; }
        internal int FreeEnds { get; private set; }
        internal int RejectedNoTarget { get; private set; }
        internal int RejectedCollapse { get; private set; }
        internal int AlreadyAnalytical { get; private set; }
        internal List<long> Skipped { get; } = new List<long>();

        /// <param name="doc">Revit document (inside an open transaction).</param>
        /// <param name="ids">Structural framing, structural columns and floors.</param>
        /// <param name="tolerance">Maximum end movement in meters.</param>
        internal void Build(Document doc, IEnumerable<ElementId> ids, double tolerance)
        {
            double tol = tolerance / 0.3048;
            var association = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
            var members = new List<Member>();
            var floors = new List<Floor>();

            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;
                if (association.HasAssociation(id)) { AlreadyAnalytical++; continue; }

                if (e is FamilyInstance fi && (fi.StructuralType == StructuralType.Beam || fi.StructuralType == StructuralType.Brace || fi.StructuralType == StructuralType.Column))
                {
                    XYZ a, b;
                    if (!Axis(doc, fi, out a, out b)) { Skipped.Add(id.GetValue()); continue; }
                    members.Add(new Member { Element = fi, A = a, B = b, IsColumn = fi.StructuralType == StructuralType.Column });
                }
                else if (e is Floor floor)
                {
                    floors.Add(floor);
                }
                else
                {
                    Skipped.Add(id.GetValue());
                }
            }

            // priority: columns first, then longer members
            members = members.OrderBy(m => m.IsColumn ? 0 : 1).ThenByDescending(m => m.A.DistanceTo(m.B)).ToList();

            // pass 0 snaps to higher priority members only; later passes let ends that are still free snap to any
            // member, and repeat because a member an end was snapped to may have moved afterwards
            for (int pass = 0; pass < 10; pass++)
            {
                int before = EndsMoved;
                for (int i = 0; i < members.Count; i++)
                {
                    int targets = pass == 0 ? i : members.Count;
                    members[i].A = Snap(members, i, members[i].A, targets, tol);
                    members[i].B = Snap(members, i, members[i].B, targets, tol);
                }
                if (pass > 0 && EndsMoved == before) break;
            }

            FreeEnds = members.Sum(m => (IsFree(members, m, m.A) ? 1 : 0) + (IsFree(members, m, m.B) ? 1 : 0));

            foreach (Member m in members)
            {
                if (m.A.DistanceTo(m.B) < 10 * Coincident) { Skipped.Add(m.Element.Id.GetValue()); continue; }

                AnalyticalMember am = AnalyticalMember.Create(doc, Line.CreateBound(m.A, m.B));
                am.StructuralRole = m.IsColumn ? AnalyticalStructuralRole.StructuralRoleColumn : AnalyticalStructuralRole.StructuralRoleBeam;
                if (am.IsValidSectionTypeId(m.Element.Symbol.Id)) am.SectionTypeId = m.Element.Symbol.Id;
                if (m.Element.StructuralMaterialId != ElementId.InvalidElementId) am.MaterialId = m.Element.StructuralMaterialId;
                association.AddAssociation(am.Id, m.Element.Id);
                MembersCreated++;
            }

            foreach (Floor floor in floors)
            {
                PlanarFace top = HostObjectUtils.GetTopFaces(floor).Select(r => floor.GetGeometryObjectFromReference(r)).OfType<PlanarFace>().OrderByDescending(f => f.Area).FirstOrDefault();
                if (top == null) { Skipped.Add(floor.Id.GetValue()); continue; }
                CurveLoop outer = top.GetEdgesAsCurveLoops().OrderByDescending(l => l.GetExactLength()).First();

                AnalyticalPanel panel = AnalyticalPanel.Create(doc, outer);
                panel.StructuralRole = AnalyticalStructuralRole.StructuralRoleFloor;
                double thickness = floor.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)?.AsDouble() ?? 0;
                if (thickness > 0) panel.Thickness = thickness;
                CompoundStructure cs = floor.FloorType.GetCompoundStructure();
                if (cs != null && cs.StructuralMaterialIndex >= 0) panel.MaterialId = cs.GetMaterialId(cs.StructuralMaterialIndex);
                association.AddAssociation(panel.Id, floor.Id);
                PanelsCreated++;
            }
        }

        /// <summary>
        /// Axis of a beam, brace or column: the location line, or base to top for vertical columns.
        /// </summary>
        internal static bool Axis(Document doc, FamilyInstance fi, out XYZ a, out XYZ b)
        {
            a = b = null;
            if (fi.Location is LocationCurve lc && lc.Curve is Line line)
            {
                a = line.GetEndPoint(0);
                b = line.GetEndPoint(1);
                return true;
            }
            if (fi.Location is LocationPoint lp && fi.StructuralType == StructuralType.Column)
            {
                var baseLevel = doc.GetElement(fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                var topLevel = doc.GetElement(fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                if (baseLevel == null || topLevel == null) return false;
                double z0 = baseLevel.Elevation + (fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0);
                double z1 = topLevel.Elevation + (fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0);
                a = new XYZ(lp.Point.X, lp.Point.Y, z0);
                b = new XYZ(lp.Point.X, lp.Point.Y, z1);
                return true;
            }
            return false;
        }

        private static XYZ ClosestOnSegment(XYZ p, XYZ a, XYZ b)
        {
            XYZ ab = b - a;
            double t = Math.Max(0, Math.Min(1, (p - a).DotProduct(ab) / ab.DotProduct(ab)));
            return a + ab * t;
        }

        /// <summary>
        /// True when no other member has an end point or passes within 1 mm of p.
        /// </summary>
        private static bool IsFree(List<Member> members, Member self, XYZ p)
        {
            foreach (Member o in members)
            {
                if (ReferenceEquals(o, self)) continue;
                if (ClosestOnSegment(p, o.A, o.B).DistanceTo(p) <= Coincident) return false;
            }
            return true;
        }

        private XYZ Snap(List<Member> members, int index, XYZ p, int targets, double tol)
        {
            Member self = members[index];
            if (!IsFree(members, self, p)) return p;
            XYZ other = ReferenceEquals(p, self.A) ? self.B : self.A;

            // On members shorter than the tolerance the closest point may belong to the joint at the other end:
            // accept a target only if the member keeps its direction and at least half of its length
            XYZ axis = p - other;
            bool Usable(XYZ c) => (c - other).DotProduct(axis) > 0 && c.DistanceTo(other) >= 0.5 * axis.GetLength();

            XYZ bestEnd = null, bestAxis = null;
            double dEnd = tol, dAxis = tol;
            for (int j = 0; j < targets; j++)
            {
                if (j == index) continue;
                Member o = members[j];
                foreach (XYZ q in new[] { o.A, o.B })
                {
                    double d = q.DistanceTo(p);
                    if (d <= dEnd && Usable(q)) { dEnd = d; bestEnd = q; }
                }
                XYZ c = ClosestOnSegment(p, o.A, o.B);
                double da = c.DistanceTo(p);
                if (da <= dAxis && Usable(c)) { dAxis = da; bestAxis = c; }
            }

            // join an end point (a node) when it is about as close as the axis, otherwise the axis (T joint)
            const double preferEnd = 0.05 / 0.3048;
            XYZ target = bestEnd != null && (bestAxis == null || dEnd <= dAxis + preferEnd) ? bestEnd : bestAxis;
            if (target == null) { RejectedNoTarget++; return p; }
            if (target.DistanceTo(other) < 10 * Coincident) { RejectedCollapse++; return p; }

            EndsMoved++;
            return target;
        }
    }
}
