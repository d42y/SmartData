# SmartData Smart Analytics Service User Guide

## Introduction

Welcome to the SmartData Smart Analytics Service! This tool helps you analyze data from your smart building, like temperature readings from sensors in classrooms. You can create analytics to process this data, such as finding the average temperature, counting sensors above a certain value, or retrieving timeseries data like temperature trends over time. This guide is for non-technical users, explaining how to set up analytics using simple steps, without needing to write complex code. You'll learn how to:

- **Run SQL queries** to fetch data from your building’s database.
- **Write C# scripts** to process data, like calculating averages.
- **Use loops** to repeat steps until a condition is met.
- **Work with variables** to store and reuse data, including lists (arrays) of values.
- **Retrieve timeseries data** to analyze trends, like temperature changes over a specific period.

We’ll use an example of analyzing classroom temperatures to show you how it works. The guide assumes you’re using a user-friendly interface (like a web or desktop app) provided by your SmartData system.

## What is the Smart Analytics Service?

The Smart Analytics Service lets you create "analytics" to process data from your smart building. Each analytic is a series of steps that can:
- Pull data from the database (e.g., get all temperatures from classroom sensors).
- Process data (e.g., calculate the average temperature).
- Check conditions and repeat steps (e.g., keep checking if the average is above 20°C).
- Retrieve timeseries data (e.g., get temperature readings over the last hour).
- Store results in variables, including lists, for use in later steps.

Analytics can run automatically:
- **On a schedule** (e.g., every minute).
- **When data changes** (e.g., when new sensor readings are added).

Results are saved in the system, can be tracked over time, or used for further analysis, like generating reports or alerts.

## Getting Started

To use the Smart Analytics Service, you need:
- Access to the SmartData user interface (web or desktop app) set up by your system administrator.
- Basic knowledge of your building’s data, like the names of tables (e.g., `Rooms`, `Sensors`) and columns (e.g., `Temperature`).
- Permission to create and run analytics (check with your administrator).

Your SmartData system connects to a database with tables like:
- **Rooms**: Contains room details (e.g., `Id`, `Name` like "Classroom A").
- **Sensors**: Contains sensor readings (e.g., `Id`, `RoomId`, `Temperature`).

## Creating an Analytics

In the SmartData interface, go to the "Analytics" section and click "Create New Analytics." You’ll need to:
1. **Name the Analytics**: Give it a clear name, like "ClassroomTemperatureAnalysis."
2. **Set the Interval**: Choose how often it runs (e.g., every 60 seconds) or trigger it when data changes (select "On Data Change").
3. **Enable Embedding (Optional)**: Check this if you want the result to be searchable with SmartData’s vector search feature.
4. **Add Steps**: Define the steps (SQL queries, C# scripts, conditions, variables, or timeseries) to process your data.

## Step Types

Each analytic is made up of steps. Here are the five types you can use:

1. **SQL Query**: Pulls data from the database, like temperatures from sensors.
2. **C# Script**: Processes data, like calculating an average or filtering values.
3. **Condition**: Checks if something is true (e.g., is the average temperature above 20°C?) and repeats steps if needed.
4. **Variable**: Stores data, like a single number or a list of values, for use in other steps.
5. **Timeseries**: Retrieves timeseries data, like temperature readings over a specific period, with optional interpolation to fill in gaps.

The result of the last step becomes the analytic’s final output, saved in the system.

## Working with Variables

Variables are like containers that hold data for use across steps. You can store single values (e.g., an average temperature), lists (arrays) of values (e.g., a history of averages), or timeseries data (e.g., a list of timestamped temperature readings).

### Defining Variables
Use a **Variable** step to create a new variable or set its value. For example:
- To create a number: Set the variable to `0`.
- To create a list: Set the variable to an empty list.
- To store timeseries data: Use a `Timeseries` step to fetch data and store it in a variable.

In the interface, select "Variable" as the step type, enter a **Script** (what the variable holds), and give it a **Name**. Examples:
- **Script**: `return 0;`  
  **Name**: `loopCount`  
  **Result**: Stores `0` in a variable called `loopCount`.
- **Script**: `return new List<object>();`  
  **Name**: `tempHistory`  
  **Result**: Creates an empty list called `tempHistory`.

### Referencing Variables
You can use variables in other steps by referring to their names:
- **In SQL Queries or Timeseries Steps**: Use curly braces, like `{avgTemp}`, to include a variable’s value in a query or expression. For example, `SELECT COUNT(*) AS SensorCount FROM Sensors WHERE Temperature > {avgTemp}` uses the value of `avgTemp`.
- **In C# Scripts or Conditions**: Use `Context["variableName"]` to access the variable. For example, `Context["avgTemp"]` gets the value of `avgTemp`.

### Working with Arrays
Arrays (or lists) let you store multiple values, like a history of temperature readings or timeseries data. You can:
- **Create an Array**: Use a `Variable` step with a script like `return new List<object>();` to create an empty array.
- **Add to an Array**: Use a `Variable` step with a name like `tempHistory[0]` to set the first item, `tempHistory[1]` for the second, and so on.
- **Access Array Items**: In C# scripts, use `Context["arrayName"][index]` to read a specific item. For example, `Context["tempHistory"][0]` gets the first item.

Example:
- **Step 1**: Create an array called `tempHistory` with `return new List<object>();`.
- **Step 2**: Store a value in `tempHistory[0]` with a `Variable` step, script `return 22.5;`, and name `tempHistory[0]`.
- **Step 3**: In a C# script, access it with `double firstTemp = (double)Context["tempHistory"][0];`.

### Writing to Variables
Every step (except `Condition`) can write its result to a variable using the **Name** field (called `ResultVariable` internally):
- **SQL Query**: The query’s result (a list of rows) is stored in the variable you name. For example, a query `SELECT Temperature FROM Sensors` with name `temperatures` stores the results in `Context["temperatures"]`.
- **C# Script**: The script’s result is stored in the named variable. For example, a script `return 42;` with name `answer` stores `42` in `Context["answer"]`.
- **Variable**: The script’s result is stored in the named variable, including array indices. For example, `return 22.5;` with name `tempHistory[0]` stores `22.5` in the first position of `tempHistory`.
- **Timeseries**: The timeseries data (a list of timestamped values) is stored in the named variable. For example, a timeseries step with name `tempSeries` stores the results in `Context["tempSeries"]`.

## Using SQL Queries

SQL queries let you fetch data from your smart building’s database. For example, you can get all temperatures from sensors in rooms with "classroom" in their name.

### How to Write a SQL Query
1. In the interface, select "SQL Query" as the step type.
2. Enter your query in the **Script** field. For example:
   ```sql
   SELECT s.Temperature FROM Sensors s JOIN Rooms r ON s.RoomId = r.Id WHERE r.Name LIKE '%classroom%'
   ```
   This gets temperatures from sensors in rooms named like "Classroom A".
3. Set a **Name** for the result, like `temperatures`. This stores the query results (a list of rows) in a variable called `temperatures`.

### Using Variables in Queries
You can include variables in your query using `{variableName}`. For example:
- If you have a variable `avgTemp` with value `22.75`, use:
  ```sql
  SELECT COUNT(*) AS SensorCount FROM Sensors WHERE Temperature > {avgTemp}
  ```
  This counts sensors with temperatures above `22.75`. The system automatically makes this safe to prevent errors or security issues.

### Tips
- Queries must start with `SELECT` and can only read data (no changes to the database).
- For the last step, ensure the query returns a single value (e.g., use `AVG`, `SUM`, `COUNT`, `MIN`, or `MAX`), like `SELECT AVG(Temperature) AS AverageTemp FROM Sensors`.
- Name the result clearly, as you’ll use it in other steps.

## Using Timeseries Data

The **Timeseries** step lets you retrieve timeseries data, such as temperature readings over a specific period, from your smart building’s database. You can fetch raw data or use interpolation to fill in gaps at regular intervals (e.g., every minute).

### How to Write a Timeseries Step
1. In the interface, select "Timeseries" as the step type.
2. Enter an expression in the **Script** field in the format:
   ```
   tableName,entityId,propertyName,start,end[,interval,method]
   ```
   - **tableName**: The database table (e.g., `Sensors`).
   - **entityId**: The ID of the entity (e.g., `Sensor123`).
   - **propertyName**: The column to retrieve (e.g., `Temperature`).
   - **start**: Start date/time (e.g., `2025-06-27T00:00:00`).
   - **end**: End date/time (e.g., `2025-06-27T23:59:59`).
   - **interval** (optional): Time interval for interpolation (e.g., `00:01:00` for 1 minute).
   - **method** (optional): Interpolation method (`None`, `Linear`, `Nearest`, `Previous`, `Next`). Default is `None` (raw data).
   Example:
   ```
   Sensors,Sensor123,Temperature,2025-06-27T00:00:00,2025-06-27T23:59:59,00:01:00,Linear
   ```
   This retrieves temperature data for `Sensor123` from the `Sensors` table, interpolated every minute using linear interpolation.
3. Set a **Name** for the result, like `tempSeries`. This stores the timeseries data (a list of timestamped values) in `Context["tempSeries"]`.

### Using Variables in Timeseries Steps
You can use variables in the expression with `{variableName}`. For example:
- If `sensorId` is a variable with value `Sensor123`, use:
  ```
  Sensors,{sensorId},Temperature,2025-06-27T00:00:00,2025-06-27T23:59:59
  ```
  This fetches timeseries data for the sensor ID stored in `sensorId`.

### Interpolation Methods
- **None**: Returns raw data points as recorded.
- **Linear**: Interpolates values linearly between data points.
- **Nearest**: Uses the nearest recorded value.
- **Previous**: Uses the last recorded value before the interval.
- **Next**: Uses the next recorded value after the interval.

### Accessing Timeseries Results
The result is a list of objects with `Timestamp` and `Value` properties. In a C# script, access it like:
```csharp
var series = Context["tempSeries"].Cast<Dictionary<string, object>>();
var latestValue = series.LastOrDefault()?["Value"]?.ToString() ?? "0";
return latestValue;
```
This gets the most recent value from the timeseries.

### Tips
- Ensure timeseries is enabled in your SmartData configuration (check with your administrator).
- Use specific date/time formats (e.g., `2025-06-27T00:00:00`).
- For the last step, ensure a `ResultVariable` is set to store the final value (e.g., the latest timeseries value).
- Use interpolation for consistent intervals in reports or charts.

## Writing C# Scripts

C# scripts let you process data, like calculating averages or filtering values. You don’t need to be a programmer—the interface makes it easy to write simple scripts.

### How to Write a C# Script
1. Select "C# Script" as the step type.
2. Enter your script in the **Script** field. You can write:
   - A single line, like `return Context["temperatures"].Cast<Dictionary<string, object>>().Average(x => (double)x["Temperature"]);` to calculate an average.
   - Multiple lines, like:
     ```csharp
     var temps = Context["temperatures"].Cast<Dictionary<string, object>>().Select(x => (double)x["Temperature"]).Where(t => t <= 100).ToList();
     var avg = temps.Any() ? temps.Average() : 0.0;
     return avg;
     ```
     This filters out temperatures above 100°C (outliers) and calculates the average.
3. Set a **Name** for the result, like `avgTemp`, to store the script’s result (e.g., `22.75`).

### Accessing Variables
Use `Context["variableName"]` to read a variable. For example:
- `Context["temperatures"]` gets the list of temperatures from a previous SQL query.
- `Context["tempSeries"]` gets timeseries data from a `Timeseries` step.
- `Context["avgTemp"]` gets a single value, like `22.75`.

For arrays, use `Context["arrayName"][index]`. For example:
- `Context["tempHistory"][0]` gets the first item in the `tempHistory` array.

### Tips
- Scripts can include multiple lines with calculations, loops, or conditions.
- The script must end with a `return` statement to provide a result (e.g., a number, list, or other value).
- Avoid using advanced programming features like file access or network calls, as they are blocked for safety.

## Creating Loops with Conditions

The **Condition** step lets you repeat earlier steps based on a condition, like checking if the average temperature is above 20°C.

### How to Create a Loop
1. Select "Condition" as the step type.
2. Enter a C# script that returns `true` or `false`. For example:
   ```csharp
   return (double)Context["avgTemp"] > 20;
   ```
   This checks if the `avgTemp` variable is above 20.
3. Set the **GoTo** field to the step number to repeat if the condition is `true`. For example, `3` to go back to step 3.
4. Set the **Max Loops** (default is 10) to limit how many times the loop can run, preventing it from running forever.
5. If the condition is `false`, the analytic moves to the next step.

### Example
If you want to keep checking temperatures until the average is 20°C or lower:
- **Step 3**: SQL query to get temperatures, named `temperatures`.
- **Step 4**: C# script to calculate average, named `avgTemp`.
- **Step 5**: Condition with script `return (double)Context["avgTemp"] > 20;` and GoTo `3`. If `true`, repeat step 3; if `false`, proceed.

### Tips
- Use a variable like `loopCount` to track how many times you’ve looped.
- Set a reasonable `Max Loops` (e.g., 10) to avoid long-running analytics.

## Writing to Variables

You can write to variables in any step except `Condition`:
- **SQL Query**: The query results are saved in the variable you name. For example, a query with name `temperatures` saves a list of rows.
- **C# Script**: The script’s result is saved in the named variable. For example, a script returning `22.75` with name `avgTemp` saves that value.
- **Variable**: Use this step to set a specific value or update an array. For example:
  - **Script**: `return 0;`  
    **Name**: `loopCount`  
    **Result**: Sets `loopCount` to `0`.
  - **Script**: `return Context["avgTemp"];`  
    **Name**: `tempHistory[0]`  
    **Result**: Saves the value of `avgTemp` in the first position of `tempHistory`.
- **Timeseries**: The timeseries results are saved in the named variable. For example, a timeseries step with name `tempSeries` saves a list of timestamped values.

### Writing to Arrays
To add to an array:
- Use a `Variable` step with a name like `tempHistory[0]` to set the first item, `tempHistory[1]` for the second, etc.
- The system automatically creates or expands the array if needed.

Example:
- **Step 1**: Create `tempHistory` with `return new List<object>();`.
- **Step 2**: Calculate `avgTemp` (e.g., `22.75`).
- **Step 3**: Save `avgTemp` to `tempHistory[0]` with script `return Context["avgTemp"];` and name `tempHistory[0]`.

## Example: Classroom Temperature Analysis

Let’s walk through a full example to analyze classroom temperatures, keeping a history of averages, retrieving timeseries data for a specific sensor, and counting sensors above the average, with a loop to repeat if the average is above 20°C.

1. **Open the Analytics Interface**:
   - Go to the "Analytics" section in your SmartData app.
   - Click "Create New Analytics."
2. **Set Up the Analytics**:
   - **Name**: `ClassroomTemperatureAnalysis`
   - **Interval**: `60` (run every 60 seconds)
   - **Embeddable**: Check this to enable vector search (optional)
3. **Add Steps**:
   - **Step 1 (Variable)**:
     - **Script**: `return new List<object>();`
     - **Name**: `tempHistory`
     - **Purpose**: Creates an empty list to store temperature averages.
   - **Step 2 (Variable)**:
     - **Script**: `return 0;`
     - **Name**: `loopCount`
     - **Purpose**: Tracks the number of loop iterations.
   - **Step 3 (SQL Query)**:
     - **Script**: `SELECT s.Temperature FROM Sensors s JOIN Rooms r ON s.RoomId = r.Id WHERE r.Name LIKE '%classroom%'`
     - **Name**: `temperatures`
     - **Purpose**: Gets temperatures from classroom sensors.
   - **Step 4 (C# Script)**:
     - **Script**:
       ```csharp
       var temps = Context["temperatures"].Cast<Dictionary<string, object>>().Select(x => (double)x["Temperature"]).Where(t => t <= 100).ToList();
       var avg = temps.Any() ? temps.Average() : 0.0;
       return avg;
       ```
     - **Name**: `avgTemp`
     - **Purpose**: Calculates the average temperature, filtering out values above 100°C.
   - **Step 5 (Variable)**:
     - **Script**: `return Context["avgTemp"];`
     - **Name**: `tempHistory[Context["loopCount"]]`
     - **Purpose**: Adds the average to `tempHistory` at the current `loopCount` index.
   - **Step 6 (Variable)**:
     - **Script**: `return (int)Context["loopCount"] +