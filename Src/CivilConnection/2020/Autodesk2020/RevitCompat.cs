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

namespace CivilConnection
{
    /// <summary>
    /// Helpers that hide Revit API differences between releases.
    /// </summary>
    internal static class RevitCompat
    {
        /// <summary>
        /// Returns the numeric value of an ElementId.
        /// ElementId.IntegerValue was replaced by the 64-bit ElementId.Value in Revit 2024 and removed in Revit 2026.
        /// </summary>
        /// <param name="id">The element id.</param>
        /// <returns></returns>
        public static long GetValue(this ElementId id)
        {
#if C2027
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }
    }
}
