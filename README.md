# What The DLL?

A simple Blazor WebAssembly application for analyzing .NET assemblies (DLL and EXE files) directly in your browser. Provides a dotPeek-like view of assembly metadata without any bells and whistles.

## Features

- 🎯 **Simple Interface** - No menu, just drag and drop or click to select a file
- 🔍 **Assembly Analysis** - View types, methods, properties, and references
- 🎨 **Modern Bootstrap Design** - Clean, card-based layout
- 🌓 **Dark Mode Support** - Automatically respects system preference
- 🚀 **Browser-Based** - All analysis happens in the browser using WebAssembly
- 📦 **No Server Processing** - Files stay on your machine

## What It Shows

- **Assembly Information**: Name, version, culture
- **References**: All referenced assemblies with versions
- **Types**: All classes and interfaces with their attributes
  - Public/Private visibility
  - Abstract, Sealed, Interface indicators
  - Properties list
  - Methods list with modifiers (public, static, abstract, virtual)

## Getting Started

### Prerequisites

- .NET 8.0 SDK or later

### Running the Application

1. Navigate to the project directory:
   ```bash
   cd PanoramicData.WhatTheDll
   ```

2. Run the application:
   ```bash
   dotnet run
   ```

3. Open your browser and navigate to the URL shown in the console (typically `http://localhost:5xxx`)

### Using the Application

1. Click anywhere on the drop zone or drag a .NET DLL/EXE file onto the page
2. The application will analyze the assembly and display all metadata
3. Click "Analyze Another DLL" to reset and analyze a different file

## Project Structure

```
PanoramicData.WhatTheDll/
├── Pages/
│   └── Home.razor              # Main page with file upload and analysis display
├── Services/
│   └── DllAnalyzer.cs          # Assembly analysis service using System.Reflection.Metadata
├── wwwroot/
│   ├── css/
│   │   └── app.css            # Custom styles including dark mode
│   └── index.html             # Main HTML with dark mode detection
└── Program.cs                  # Application entry point
```

## Technology Stack

- **Blazor WebAssembly** - Client-side web framework
- **Bootstrap 5** - UI framework with dark mode support
- **System.Reflection.Metadata** - Assembly metadata reading

## Notes

- Maximum file size: 50 MB
- Only .NET assemblies (DLL/EXE files) are supported
- All processing happens client-side in the browser
- Files are not uploaded to any server

## Building for Production

To build the application for production:

```bash
dotnet publish -c Release
```

The output will be in `bin/Release/net8.0/browser-wasm/publish/wwwroot/`

## License

This project was created by PanoramicData.
