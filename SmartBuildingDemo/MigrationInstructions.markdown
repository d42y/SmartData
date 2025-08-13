# Entity Framework Core Migration Instructions for SmartBuildingDemo

This guide provides instructions for creating, applying, removing, and updating Entity Framework Core migrations for the `SmartBuildingDemo` application using the `AppDbContext` with SQL Server. The migrations will be output to a `Migrations` folder in the project directory.

## Prerequisites

- **.NET SDK**: Ensure .NET 8.0 SDK or later is installed.
- **EF Core CLI Tools**: Install the EF Core CLI tools if not already installed:
  ```bash
  dotnet tool install --global dotnet-ef
  ```
- **NuGet Packages**: Ensure the following packages are included in your `.csproj` file:
  ```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="8.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="8.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.0.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="8.0.0" />
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.17.0" />
    <PackageReference Include="SqlKata" Version="2.4.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Scripting" Version="4.8.0" />
  </ItemGroup>
  ```
- **SQL Server**: Ensure SQL Server is running locally or update the connection string in `AppDbContextFactory` and `CreateHostBuilder` in `SmartBuildingDemo.cs` to match your SQL Server instance:
  ```csharp
  "Server=your_server;Database=SmartBuildingDemo;User Id=your_user;Password=your_password;TrustServerCertificate=True;"
  ```
- **Project Setup**: Verify that the project contains the `AppDbContext` and `AppDbContextFactory` as defined in the `SmartBuildingDemo.cs` code, and that it builds successfully:
  ```bash
  dotnet build
  ```

## Creating a Migration

To generate a new migration and output it to the `Migrations` folder, run the following command from the project directory (where the `.csproj` file is located):

```bash
dotnet ef migrations add InitialCreate --context AppDbContext --output-dir Migrations
```

- **Explanation**:
  - `InitialCreate`: The name of the migration. You can use any descriptive name (e.g., `AddSensorTable` for subsequent migrations).
  - `--context AppDbContext`: Specifies the `AppDbContext` as the target context, as defined in the demo code.
  - `--output-dir Migrations`: Directs EF Core to place the migration files (e.g., `YYYYMMDDHHMMSS_InitialCreate.cs` and a designer file) in the `Migrations` folder. If the folder doesn’t exist, it will be created.

This command generates migration files that define the database schema, including tables for `Buildings`, `Sensors`, and SmartData system tables (`sysChangeLog`, `sysTimeseriesBaseValues`, `sysAnalytics`, etc.).

## Applying Migrations (Initializing/Updating the Database)

To apply the migration and create or update the database schema, run:

```bash
dotnet ef database update --context AppDbContext
```

- **Explanation**:
  - This command applies all pending migrations in the `Migrations` folder to the SQL Server database specified in the connection string (`SmartBuildingDemo`).
  - It creates the database if it doesn’t exist and sets up the schema, including tables and relationships.
  - The `--context AppDbContext` ensures the correct context is used.

## Removing a Migration

If you need to remove a migration (e.g., due to errors or changes in requirements), follow these steps:

1. **Ensure No Pending Changes**:
   - If the migration has already been applied to the database, revert it first (see "Reverting a Migration" below).
   - If the migration hasn’t been applied, you can safely remove it.

2. **Remove the Migration**:
   Run the following command to remove the most recent migration from the `Migrations` folder:

   ```bash
   dotnet ef migrations remove --context AppDbContext
   ```

   - **Explanation**:
     - This deletes the latest migration files from the `Migrations` folder (e.g., `YYYYMMDDHHMMSS_InitialCreate.cs` and its designer file).
     - The `--context AppDbContext` specifies the target context.
     - This command only works if the migration hasn’t been applied to the database. If it has, you must revert it first.

3. **Verify Removal**:
   - Check the `Migrations` folder to ensure the migration files are deleted.
   - If you have version control (e.g., Git), ensure the removed files are staged for commit.

## Updating a Migration

To update a migration after modifying the entity models (e.g., adding a new property to `Sensor`) or the `OnModelCreating` method in `AppDbContext`, follow these steps:

1. **Modify the Model**:
   - Update the entity classes (`Building`, `Sensor`) or the `OnModelCreating` method in `AppDbContext` as needed.
   - Ensure the project builds successfully:
     ```bash
     dotnet build
     ```

2. **Generate a New Migration**:
   Create a new migration to reflect the changes, specifying the `Migrations` folder:

   ```bash
   dotnet ef migrations add UpdateModel --context AppDbContext --output-dir Migrations
   ```

   - **Explanation**:
     - `UpdateModel`: A descriptive name for the new migration (e.g., `AddSensorLocation`).
     - `--context AppDbContext`: Targets the `AppDbContext`.
     - `--output-dir Migrations`: Ensures the new migration files are placed in the `Migrations` folder.
     - EF Core will detect changes in the model and generate the necessary migration code.

3. **Apply the Migration**:
   Update the database to apply the new migration:

   ```bash
   dotnet ef database update --context AppDbContext
   ```

   - This applies the new migration to the database, updating the schema to reflect the model changes.

## Reverting a Migration

If you need to revert a migration (e.g., to undo an applied migration), you can roll back to a previous migration or to no migrations (empty database):

1. **Revert to a Specific Migration**:
   To revert to a previous migration (e.g., to undo `UpdateModel` and go back to `InitialCreate`), run:

   ```bash
   dotnet ef database update InitialCreate --context AppDbContext
   ```

   - **Explanation**:
     - `InitialCreate`: The name of the migration to revert to. Use `0` to revert to an empty database (drops all tables created by migrations).
     - This updates the database schema to match the specified migration, effectively undoing later migrations.
     - Be cautious, as this may drop tables or columns, leading to data loss.

2. **Revert to Empty Database**:
   To remove all migrations and reset the database to an empty state:

   ```bash
   dotnet ef database update 0 --context AppDbContext
   ```

   - This drops all tables created by migrations. Use with caution, as it will delete all data in the migrated tables.

3. **Remove Unapplied Migrations (Optional)**:
   After reverting, you can remove unapplied migrations from the `Migrations` folder:

   ```bash
   dotnet ef migrations remove --context AppDbContext
   ```

   - Repeat this command to remove each unapplied migration until the `Migrations` folder matches the database state.

## Troubleshooting

- **Migration Creation Fails**:
  - **Error**: "No DbContext named 'AppDbContext' was found."
    - **Solution**: Ensure `AppDbContextFactory` is correctly defined and the project builds successfully (`dotnet build`). Verify the `--context AppDbContext` flag is used.
  - **Error**: "The relationship from 'Sensor' to 'Building' with foreign key properties {'BuildingId' : string} cannot target the primary key {'Id' : Guid}."
    - **Solution**: Ensure `Sensor.BuildingId` is a `Guid` to match `Building.Id`, as fixed in the updated `SmartBuildingDemo.cs`.
- **Database Connection Issues**:
  - Verify SQL Server is running and accessible. Test the connection string using SQL Server Management Studio or a similar tool.
  - Update the connection string in `AppDbContextFactory` and `CreateHostBuilder` if needed:
    ```csharp
    "Server=your_server;Database=SmartBuildingDemo;User Id=your_user;Password=your_password;TrustServerCertificate=True;"
    ```
- **Migration Files Not in `Migrations` Folder**:
  - Ensure the `--output-dir Migrations` flag is included when running `dotnet ef migrations add`.
  - Check that the project’s `.csproj` file specifies the correct migrations assembly (handled automatically by `typeof(AppDbContextFactory).Assembly.GetName().Name` in the demo).
- **Data Loss on Revert**:
  - Reverting migrations may drop tables or columns, causing data loss. Back up the database before reverting:
    ```bash
    sqlcmd -S localhost -Q "BACKUP DATABASE SmartBuildingDemo TO DISK = 'C:\Backups\SmartBuildingDemo.bak'"
    ```

## Additional Notes

- **Project Directory**: Run all commands from the project directory containing the `.csproj` file.
- **Connection String Consistency**: Ensure the connection string in `AppDbContextFactory` and `CreateHostBuilder` is identical to avoid runtime issues.
- **Subsequent Migrations**: For future model changes, repeat the "Updating a Migration" steps with a new migration name (e.g., `AddNewFeature`).
- **Version Control**: Commit migration files to version control (e.g., Git) to track schema changes:
  ```bash
  git add Migrations/
  git commit -m "Added InitialCreate migration"
  ```
- **Running the Application**: After applying migrations, run the application to test the schema:
  ```bash
  dotnet run
  ```

These instructions ensure that migrations are generated in the `Migrations` folder, applied to initialize or update the database, and can be removed or reverted as needed for the `SmartBuildingDemo` application.