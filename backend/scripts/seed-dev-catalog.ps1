<#
.SYNOPSIS
  Loads a small LOCAL TEST catalog into a development API.

.DESCRIPTION
  Creates the five products shown on the current zladovce.com homepage (names, categories and
  prices as published there) so the storefront and admin have something real-looking to show
  during local development. This is test data only:
    - package sizes are not published on the current site, so jams, juice and compote use a
      neutral "1 kom" package until the business supplies real sizes;
    - descriptions are marked as test data and make no product claims;
    - no photos are uploaded (the current site uses stock photos, which are not reused).

  Uses the admin API with the development auth headers, so it only works against an API running
  in Development with Auth:DevBypass=true. Safe to run more than once: existing slugs are skipped.

.EXAMPLE
  powershell -File scripts/seed-dev-catalog.ps1
  powershell -File scripts/seed-dev-catalog.ps1 -ApiBaseUrl http://localhost:5080/api/v1
#>
param(
  [string]$ApiBaseUrl = 'http://localhost:5080/api/v1',
  [string]$AdminEmail = 'seed@example.com'
)

$ErrorActionPreference = 'Stop'
$headers = @{ 'X-Dev-User-Email' = $AdminEmail; 'X-Dev-User-Role' = 'Owner' }
$testNote = 'Testni podatak za lokalni razvoj. Naziv i cena preuzeti sa postojećeg sajta; pakovanje i opis treba da potvrdi Zladovce.'

function Invoke-Api([string]$Method, [string]$Path, $Body) {
  $params = @{ Method = $Method; Uri = "$ApiBaseUrl$Path"; Headers = $headers; ContentType = 'application/json; charset=utf-8' }
  if ($null -ne $Body) { $params.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10)) }
  Invoke-RestMethod @params
}

function Get-ProblemCode($ErrorRecord) {
  try { ($ErrorRecord.ErrorDetails.Message | ConvertFrom-Json).code } catch { $null }
}

$categories = @(
  @{ name = 'Džemovi';   slug = 'dzemovi';   sortOrder = 10 },
  @{ name = 'Sokovi';    slug = 'sokovi';    sortOrder = 20 },
  @{ name = 'Kompoti';   slug = 'kompoti';   sortOrder = 30 },
  @{ name = 'Sveže voće'; slug = 'sveze-voce'; sortOrder = 40 }
)

$products = @(
  @{ category = 'dzemovi';    name = 'Džem od borovnice';             slug = 'dzem-od-borovnice';             sku = 'TEST-DZ-BOR'; variant = 'Pakovanje';  unitAmount = 1; unit = 'kom'; price = 890 },
  @{ category = 'dzemovi';    name = 'Džem od maline';                slug = 'dzem-od-maline';                sku = 'TEST-DZ-MAL'; variant = 'Pakovanje';  unitAmount = 1; unit = 'kom'; price = 850 },
  @{ category = 'sokovi';     name = 'Sok od borovnice';              slug = 'sok-od-borovnice';              sku = 'TEST-SO-BOR'; variant = 'Pakovanje';  unitAmount = 1; unit = 'kom'; price = 720 },
  @{ category = 'kompoti';    name = 'Kompot od jabuke i borovnice';  slug = 'kompot-od-jabuke-i-borovnice';  sku = 'TEST-KO-JAB'; variant = 'Pakovanje';  unitAmount = 1; unit = 'kom'; price = 650 },
  @{ category = 'sveze-voce'; name = 'Sveža borovnica';               slug = 'sveza-borovnica';               sku = 'TEST-SV-BOR'; variant = '1 kg';       unitAmount = 1; unit = 'kg';  price = 1200 }
)

# Categories: reuse active ones with the same slug, create the rest.
# (Saved as UTF-8 with BOM so Windows PowerShell 5.1 reads the Serbian letters correctly.)
$existing = @{}
foreach ($c in (Invoke-Api GET '/admin/categories' $null).items) { $existing[$c.slug] = $c.id }

$categoryIds = @{}
foreach ($c in $categories) {
  if ($existing.ContainsKey($c.slug)) {
    $categoryIds[$c.slug] = $existing[$c.slug]
    Write-Host "Kategorija postoji: $($c.name)"
    continue
  }
  $created = Invoke-Api POST '/admin/categories' @{
    name = $c.name; slug = $c.slug; description = $null; imageKey = $null
    seoTitle = $null; seoDescription = $null; sortOrder = $c.sortOrder; isActive = $true
  }
  $categoryIds[$c.slug] = $created.id
  Write-Host "Kategorija kreirana: $($c.name)"
}

foreach ($p in $products) {
  $found = (Invoke-Api GET "/admin/products?q=$($p.sku)&pageSize=100" $null).items | Where-Object { $_.sku -eq $p.sku }
  if ($found) { Write-Host "Proizvod postoji: $($p.name)"; continue }
  try {
    Invoke-Api POST '/admin/products' @{
      categoryId = $categoryIds[$p.category]; name = $p.name; slug = $p.slug; sku = $p.sku
      shortDescription = $testNote; description = "<p>$testNote</p>"
      isActive = $true; isFeatured = $true
      variants = @(@{
        name = $p.variant; sku = "$($p.sku)-P1"; unitAmount = $p.unitAmount; measurementUnit = $p.unit
        regularPrice = $p.price; discountPrice = $null; isActive = $true
        initialStock = 20; lowStockThreshold = 5
      })
    } | Out-Null
    Write-Host "Proizvod kreiran: $($p.name)"
  }
  catch {
    $code = Get-ProblemCode $_
    if ($code -in 'SLUG_TAKEN', 'SKU_TAKEN') { Write-Host "Proizvod postoji: $($p.name)" }
    else { throw }
  }
}

Write-Host 'Gotovo. Testni katalog je učitan.'
