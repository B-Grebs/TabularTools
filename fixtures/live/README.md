# LiveCheck: a synthetic model for live verification

`LiveCheck.SemanticModel` is a tiny model built only from `DATATABLE` calculated tables, so it has known values and
no data source: deploying it needs no gateway or credentials, and a refresh just recalculates it. It exists for
[`scripts/verify-live.ps1`](../../scripts/verify-live.ps1), which checks TabularTools' live features against it.

| Object | Value |
|---|---|
| `Facts` | 4 rows: East 2023 (100, cost 60), West 2023 (200, 150), East 2024 (120, 70), West 2024 (250, 160) |
| `[Base Amount]` | `SUM(Facts[Amount])` = 670 (East 220, West 450) |
| `[Dependent]` | `[Base Amount] * 2` = 1340 |
| `[Margin]` | `DIVIDE([Base Amount] - SUM(Facts[Cost]), [Base Amount])` |
| `Facts[Double Amount]` | calculated column `Facts[Amount] * 2` (sum 1340) |
| `Years` | 2 rows, to test a measure name on another table |
| Role `East Only` | `Facts[Region] = "East"` |

## Deploy it (once)

TabularTools never writes to a service, so deploy with another tool, to a workspace on Premium, PPU or Fabric capacity
with the XMLA endpoint set to at least read:

- **Tabular Editor 2** (free): *File → Open → From Folder…* and pick `LiveCheck.SemanticModel/definition`, then
  *Model → Deploy…* to `powerbi://api.powerbi.com/v1.0/myorg/<workspace>` (XMLA read-write needed for the deploy only).
- **Fabric Git integration**: commit the `LiveCheck.SemanticModel` folder to a connected repository and sync the
  workspace.

Then refresh the model once in the service (or *Process → Recalc* in Tabular Editor) so the calculated tables have rows.

## Run the checks (Windows, PowerShell 7)

```powershell
./scripts/verify-live.ps1 -Endpoint 'powerbi://api.powerbi.com/v1.0/myorg/<workspace>'
```

The identity needs Build permission on the model; the RLS check (`East Only`) needs write or admin rights. The script
writes `live-verification.md` (paste it into `docs/milestones/M4.md`) and `livecheck.recordings.yaml`.
