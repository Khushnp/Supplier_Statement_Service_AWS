# Supplier Statement Service AWS

Production-ready C# console application targeting **.NET Framework 4.8** to extract supplier statement data via **AWS Textract**.

## Supported input files
- PDF (`.pdf`)
- Images (`.png`, `.jpg`, `.jpeg`)
- Excel (`.xlsx`, `.xls`)
- CSV (`.csv`)

## Flow
1. Prompt for full file path.
2. Validate input and detect file type.
3. Convert Excel/CSV to temporary PDF (Interop first, open-source fallback).
4. Send image to AWS Textract (`AnalyzeDocument` with `TABLES` + `FORMS`).
5. For PDF/TIFF, first try `AnalyzeDocument(Bytes)`; if unsupported, use async Textract analysis (`StartDocumentAnalysis` + polling) via S3 fallback.
6. Parse extracted data into structured JSON.
7. Print JSON to console and save `<inputfilename>.json` next to source file.

## AWS configuration
Set environment variables before running:
- `AWS_ACCESS_KEY`
- `AWS_SECRET_KEY`
- `AWS_REGION`
- `AWS_TEXTRACT_S3_BUCKET` (optional, but required for PDF/TIFF async fallback when sync bytes call is unsupported)

## Project structure
- `Program.cs` - Console workflow and output handling.
- `Services/FileProcessor.cs` - File routing and temp cleanup.
- `Services/ExcelToPdfConverter.cs` - Interop + fallback conversion.
- `Services/TextractService.cs` - AWS Textract API integration.
- `Services/TextractParser.cs` - Generic heuristic parser for supplier/customer/invoices.
- `Models/SupplierStatement.cs` and `Models/Invoice.cs` - Output models.

## NuGet packages
- `AWSSDK.Textract`
- `Newtonsoft.Json`
- `ClosedXML`
- `itext7`
- `System.Drawing.Common`
- `Microsoft.Office.Interop.Excel` (optional, preferred conversion path)

## Build/run
Build with Visual Studio 2022 (or MSBuild) using **.NET Framework 4.8 Developer Pack** on Windows.
