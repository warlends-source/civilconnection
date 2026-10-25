# CivilConnection and CivilPython
CivilConnection is a Dynamo for Revit package for Autodesk® Revit and Autodesk® Civil 3D.

CivilPython is a command for Autodesk® Civil 3D that enables the execution of Python scripts accessing AutoCAD and Civil 3D .NET APIs.

## CivilConnection Main Features
Here are some features enabled by CivilConnection:
* Enables the exchange of information between Civil 3D, Dynamo and Revit.
* Reads the information embedded in rich linear objects such as Civil 3D alignments, corridors or feature lines and creates proxy elements in Dynamo. In turn the proxy elements can be used to establish dynamic relationships to drive the creation of discrete Revit elements (i.e. single point family instances, line based objects such as structural framing or MEP segments, complex objects such as adaptive components, floors, walls, Revit link instances).
* Provides features to update the location, orientation and metadata of Revit elements against a Civil 3D input.
* Reads the shapes and links of the Civil 3D Corridors and create and update modifiable Revit families without any tessellation. This enables to further the detailing in Revit, use parts and rebar, assign custom materials to the objects and preparing it for the construction phase.
* Creates basic AutoCAD entities such as layers, points, line, arcs, polylines 2d and 3d, region, solids.
* Creates basic Civil 3D entities such as point groups or alignments to enable the creation of TIN surfaces or providing targets for corridors.
* Performs boolean operations between AutoCAD solids; this functionalities are used to add details to the Civil 3D models with discrete elements or performing subtraction that preserve the individual solids involved.
* Imports geometry elements generated in Dynamo into Civil 3D via SAT Export / Import.
* Imports and updates the solids in the geometry of Revit elements into Civil 3D via the link Element functionality.
* Gets or sets the parameters of the subassemblies in a corridor and force the corridor to rebuild.
* Sends commands to the Civil 3D command line (this feature can be used to launch CivilPython).

## CivilPython Main Features
Here are some features enabled by CivilPython:
* Uses the IronPython 2.7 that comes with Dynamo for Revit (in alternative you can download IronPython separately).
* Uses .NET API with the same interpreted approach without the need to compile the code into a .DLL assembly.
* The .py code can be developed in any external Python IDE.
* The code can be copied and pasted into a Python Script node in Dynamo for Civil 3D (2020 onward)
* Provides a user interface command and a command line version that can be used to select Python scripts interactively or to specify their path on the file system via CivilConnection.
* CivilPython runs for Civil 3D 2016 onward.

**IMPORTANT NOTE**
The long term support for Python 2.7 has ended in 2020. As a policy, there cannot be new releases of tools leveraging Python 2.7, CivilPython is one of them. This means that the functionalities of CivilPython that are leveraging IronPython 2.7 need to be migrated to use CPython that is based on Python 3.7. Currently, CPython does not yet offer a seamless experience and there are constructs that are not successfully recognized if compared with IronPython (e.g., it does not implement operators between objects, it forces to change syntax on existing code, etc.).
As Dynamo for Civil 3D is available since release 2020, it has been decided to disable the functionalities connected to the Python scripting in CivilPython and only enable the hidden commands that are used by CivilConnection.
As a result, when calling "python" at the command line from Civil 3D 2022+ with the latest version of CivilPython you will get this message instead:

"Congratulations! CivilConnection can now be used.
You can now close this message.

NOTE: The execution of Python scripts via this command has been disabled in Civil 3D 2022.
IronPython 2.7 longer term support has been discontinued.
It is recommended to migrate your previous Python code to CPython 3.7.
They can be used as modules in Python Script nodes in Dynamo for Civil 3D.
Currently there are some limitations with CPython and an investigation on how to migrate CivilPython is undergoing.
Stay tuned for more updates."

If there is an older version of CivilPython installed on the machine it will continue to work.

## How to use
See ./Doc/Linear Structures Workflow Guide.pdf

In ./Compiled there are the releases of the CivilConnection Dynamo package as well as CivilPython ready to use.

## Release
The release numbers for the package correspond to the major release numbers of Civil 3D and Revit it will run against

**Note that CivilConnection and Civil 3D with different release numbers are not compatible.**

* Civil 3D and Revit 2027 - CivilConnection 2027 - Autodesk2027.dll (.NET 10, Dynamo for Revit 4.1)
* Civil 3D and Revit 2023 - CivilConnection 2023 - Autodesk2023.dll
* Civil 3D and Revit 2022 - CivilConnection 2022 - Autodesk2022.dll
* Civil 3D and Revit 2021 - CivilConnection 2021 - Autodesk2021.dll
* Civil 3D and Revit 2020 - CivilConnection 2020 - Autodesk2020.dll
* Civil 3D and Revit 2019 - CivilConnection 2019 - Autodesk2019.dll [End of Life]
* Civil 3D and Revit 2018 - CivilConnection 2018 - Autodesk2018.dll [End of Life]
* Civil 3D and Revit 2017 - CivilConnection 2017 - Autodesk2017.dll [End of Life]

## Building for 2027
Civil 3D 2027, Revit 2027 and Dynamo for Revit 4.1 run on .NET 10, so the 2027 build uses SDK-style projects that share the source of the legacy projects (compiled with the `C2027` symbol). It requires the .NET 10 SDK and local installs of Revit 2027 and Civil 3D 2027.

```
dotnet build Src/CivilConnection/2027/Autodesk2027/Autodesk2027.csproj -c Release
dotnet build Src/CivilPython/2027/CivilPython2027.csproj -c Release
```

The projects look for the products in `C:\Program Files\Autodesk\Revit 2027` and `C:\Program Files\Autodesk\AutoCAD 2027`; pass `-p:RevitPath=...` and `-p:C3DPath=...` if they are installed elsewhere.

* The CivilConnection build creates the Dynamo package in `bin\Release\CivilConnection2027` and copies it to `%AppData%\Dynamo\Dynamo Revit\4.1\packages` (add `-p:DeployPackage=false` to skip the copy).
* The CivilPython build creates `bin\Release\CivilPython.bundle`. Copy that folder to `%APPDATA%\Autodesk\ApplicationPlugins` (or `C:\Program Files\Autodesk\ApplicationPlugins` for all users) so that Civil 3D 2027 can run the commands CivilConnection uses. Civil 3D 2027 does not scan `C:\ProgramData\Autodesk\ApplicationPlugins`. Python scripting stays disabled in this release.

## Robot Structural Analysis (2027)
When Robot Structural Analysis Professional 2027 is installed, the 2027 package also contains the `CivilConnection.Robot.RobotModel` nodes, which drive Robot through its COM API (`-p:RobotPath=...` points the build to another install folder; `-p:WithRobot=false` leaves the nodes out).

* **Connection**: `RobotModel.Connect` attaches to the running Robot session (open Robot with a project first). `lengthUnitToMeters` converts Dynamo units to meters (negative = read from the Revit document) and `origin` moves the model close to (0,0,0), which matters for Civil 3D coordinates.
* **Civil 3D to Robot**: `BarsFromFeaturelines`, `BarsFromAlignment` and `DeckPanelsBetweenFeaturelines`, plus generic `CreateNodes`, `CreateBars` and `CreatePanels` for any Dynamo geometry (e.g. `CivilSurface.GetTrianglesSurfaces`).
* **Revit to Robot**: `FromRevitAnalytical` sends analytical members and panels (`RevitAnalyticalElements`), or the physical elements they belong to. Without an analytical model it falls back to the physical geometry (`RevitStructuralElements`): location lines of beams and braces, base to top of columns, top face of floors and straight structural walls. Materials come from the Revit structural assets; sections are solid rectangles, Robot database sections of the same name, or sections defined by the properties of the Revit type or, when the family has none, computed from the member cross-section. Physical location lines rarely meet at the joints, so use the analytical model for analysis.
* **Labels, loads and combinations**: `CreateMaterial`, `CreateRectangularSection`, `CreatePanelThickness` (with elastic foundation), `CreateSupport`, `CreateLoadCase`, `AddSelfWeight`, `AddNodalForces`, `AddBarUniformLoads`, `AddPanelUniformLoads`, `CreateCombination`, `GenerateMesh`, `Calculate`, `SaveAs`.
* **Results**: `NodeDisplacements`, `NodeReactions`, `BarForces`, `PanelForces` and `NearestNodes`.

Loads and results are in SI units (N, m, Pa). Every run of a graph adds objects to the Robot model, so use `Connect` with `newProject = true` to rebuild it from scratch. Connect `waitFor` inputs to control the order of the steps. Bar orientation (section rotation) and member releases are not transferred yet.

Example graphs: [Examples/Dynamo/2027](Examples/Dynamo/2027).

## License
See [LICENSING.md](LICENSING.md)

## Contributions
Read our contribution guidelines here: [CONTRIBUTING.md](CONTRIBUTING.md)
