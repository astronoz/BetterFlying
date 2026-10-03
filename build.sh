#!/bin/bash

echo "Building DynamicFlight..."

# Clean
dotnet clean

# Build with net8.0 and ignore version conflicts
dotnet build -c Release -f net8.0

if [ $? -eq 0 ]; then
    echo "Build successful!"
    echo "DynamicFlight.dll: bin/DynamicFlight.dll"
else
    echo "Build failed!"
fi