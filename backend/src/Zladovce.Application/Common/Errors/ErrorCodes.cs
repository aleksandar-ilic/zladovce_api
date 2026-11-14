namespace Zladovce.Application.Common.Errors;

/// <summary>Stable machine codes returned in the <c>code</c> member of every error response.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string BadRequest = "BAD_REQUEST";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string AdminInactive = "ADMIN_INACTIVE";
    public const string AdminNotProvisioned = "ADMIN_NOT_PROVISIONED";
    public const string NotFound = "NOT_FOUND";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string Conflict = "CONFLICT";
    public const string ConcurrentEdit = "CONCURRENT_EDIT";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";

    // Catalog and categories
    public const string SlugTaken = "SLUG_TAKEN";
    public const string SkuTaken = "SKU_TAKEN";
    public const string CategoryHasProducts = "CATEGORY_HAS_PRODUCTS";
    public const string CategoryArchived = "CATEGORY_ARCHIVED";
    public const string ProductArchived = "PRODUCT_ARCHIVED";
    public const string LastVariant = "LAST_VARIANT";
    public const string LastActiveVariant = "LAST_ACTIVE_VARIANT";
    public const string NoActiveVariant = "NO_ACTIVE_VARIANT";

    // Inventory
    public const string InsufficientStock = "INSUFFICIENT_STOCK";

    // Media
    public const string UploadNotFound = "UPLOAD_NOT_FOUND";
    public const string InvalidImage = "INVALID_IMAGE";
    public const string ImageNotReady = "IMAGE_NOT_READY";
    public const string ImageAlreadyAttached = "IMAGE_ALREADY_ATTACHED";
    public const string ImageLimitReached = "IMAGE_LIMIT_REACHED";

    // Checkout and orders
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string CartChanged = "CART_CHANGED";
    public const string MinimumOrderNotMet = "MINIMUM_ORDER_NOT_MET";
    public const string ShippingDisabled = "SHIPPING_DISABLED";
    public const string InvalidStatusTransition = "INVALID_STATUS_TRANSITION";
    public const string OrderNotDelivered = "ORDER_NOT_DELIVERED";

    // Shipping
    public const string CourierNameTaken = "COURIER_NAME_TAKEN";
}

/// <summary>Default Serbian (Latin) texts for the generic error codes.</summary>
public static class ErrorTexts
{
    public const string ValidationTitle = "Neispravni podaci";
    public const string ValidationDetail = "Proverite unete podatke i pokušajte ponovo.";

    public const string BadRequestTitle = "Neispravan zahtev";
    public const string BadRequestDetail = "Zahtev nije moguće obraditi.";

    public const string UnauthorizedTitle = "Niste prijavljeni";
    public const string UnauthorizedDetail = "Prijavite se da biste nastavili.";

    public const string ForbiddenTitle = "Pristup odbijen";
    public const string ForbiddenDetail = "Nemate dozvolu za ovu radnju.";

    public const string AdminInactiveTitle = "Nalog nije aktivan";
    public const string AdminInactiveDetail = "Vaš administratorski nalog je deaktiviran. Obratite se vlasniku prodavnice.";

    public const string AdminNotProvisionedTitle = "Nalog nije podešen";
    public const string AdminNotProvisionedDetail = "Vaš administratorski nalog nije moguće povezati sa prodavnicom. Obratite se vlasniku prodavnice.";

    public const string NotFoundTitle = "Nije pronađeno";
    public const string NotFoundDetail = "Traženi sadržaj ne postoji.";

    public const string MethodNotAllowedTitle = "Metoda nije dozvoljena";
    public const string MethodNotAllowedDetail = "Ova radnja nije podržana na traženoj adresi.";

    public const string ConflictTitle = "Radnja nije moguća";
    public const string ConflictDetail = "Radnju nije moguće izvršiti u trenutnom stanju podataka.";

    public const string ConcurrentEditTitle = "Podaci su u međuvremenu izmenjeni";
    public const string ConcurrentEditDetail = "Neko drugi je izmenio ove podatke. Osvežite stranicu i pokušajte ponovo.";

    public const string UnsupportedMediaTypeTitle = "Nepodržan format";
    public const string UnsupportedMediaTypeDetail = "Format poslatih podataka nije podržan.";

    public const string PayloadTooLargeTitle = "Zahtev je prevelik";
    public const string PayloadTooLargeDetail = "Poslati podaci su preveliki.";

    public const string RateLimitedTitle = "Previše zahteva";
    public const string RateLimitedDetail = "Poslali ste previše zahteva. Pokušajte ponovo malo kasnije.";

    public const string InternalErrorTitle = "Greška na serveru";
    public const string InternalErrorDetail = "Došlo je do neočekivane greške. Pokušajte ponovo kasnije.";

    public const string ServiceUnavailableTitle = "Servis trenutno nije dostupan";
    public const string ServiceUnavailableDetail = "Servis trenutno nije dostupan. Pokušajte ponovo kasnije.";

    public const string SlugTakenTitle = "Adresa je zauzeta";
    public const string SlugTakenDetail = "Ova adresa (slug) se već koristi. Izaberite drugu.";

    public const string SkuTakenTitle = "Šifra je zauzeta";
    public const string SkuTakenDetail = "Ova šifra (SKU) se već koristi. Izaberite drugu.";

    public const string CategoryHasProductsTitle = "Kategorija ima proizvode";
    public const string CategoryHasProductsDetail = "Kategoriju nije moguće arhivirati dok sadrži proizvode koji nisu arhivirani.";

    public const string CategoryArchivedTitle = "Kategorija je arhivirana";
    public const string CategoryArchivedDetail = "Kategorija proizvoda je arhivirana. Izaberite drugu kategoriju.";

    public const string ProductArchivedTitle = "Proizvod je arhiviran";
    public const string ProductArchivedDetail = "Arhivirani proizvod nije moguće menjati. Prvo ga vratite iz arhive.";

    public const string LastVariantTitle = "Poslednja varijanta";
    public const string LastVariantDetail = "Proizvod mora imati najmanje jednu varijantu.";

    public const string LastActiveVariantTitle = "Poslednja aktivna varijanta";
    public const string LastActiveVariantDetail = "Aktivan proizvod mora imati najmanje jednu aktivnu varijantu. Prvo deaktivirajte proizvod.";

    public const string NoActiveVariantTitle = "Nema aktivne varijante";
    public const string NoActiveVariantDetail = "Proizvod nije moguće aktivirati dok nema nijednu aktivnu varijantu.";

    public const string InsufficientStockTitle = "Nedovoljno na stanju";
    public const string InsufficientStockDetail = "Na stanju nema dovoljno proizvoda za ovu promenu.";

    public const string UploadNotFoundTitle = "Slika nije pronađena";
    public const string UploadNotFoundDetail = "Otpremljena slika nije pronađena. Otpremite sliku ponovo.";

    public const string InvalidImageTitle = "Neispravna slika";
    public const string InvalidImageDetail = "Datoteka nije ispravna slika. Dozvoljeni formati su JPEG, PNG i WebP, do 10 MB.";

    public const string ImageNotReadyTitle = "Slika se još obrađuje";
    public const string ImageNotReadyDetail = "Obrada slike još nije završena. Pokušajte ponovo za nekoliko sekundi.";

    public const string ImageAlreadyAttachedTitle = "Slika je već dodata";
    public const string ImageAlreadyAttachedDetail = "Ova slika je već dodata. Otpremite novu sliku.";

    public const string ImageLimitReachedTitle = "Previše slika";
    public const string ImageLimitReachedDetail = "Proizvod već ima najveći dozvoljeni broj slika.";

    public const string IdempotencyKeyReusedTitle = "Zahtev je već iskorišćen";
    public const string IdempotencyKeyReusedDetail = "Ovaj ključ porudžbine je već iskorišćen za drugačiju porudžbinu. Osvežite stranicu i pokušajte ponovo.";

    public const string CartChangedTitle = "Korpa je promenjena";
    public const string CartChangedDetail = "Cene ili dostupnost proizvoda u korpi su se promenile. Proverite korpu i potvrdite porudžbinu ponovo.";

    public const string CheckoutInsufficientStockDetail = "Nekih proizvoda više nema u traženoj količini. Proverite korpu i potvrdite porudžbinu ponovo.";

    public const string MinimumOrderNotMetTitle = "Minimalni iznos porudžbine nije dostignut";
    public const string MinimumOrderNotMetDetail = "Iznos porudžbine je manji od minimalnog. Dodajte još proizvoda u korpu.";

    public const string ShippingDisabledTitle = "Poručivanje trenutno nije moguće";
    public const string ShippingDisabledDetail = "Dostava je trenutno isključena, pa porudžbine nije moguće primiti. Pokušajte ponovo kasnije.";

    public const string InvalidStatusTransitionTitle = "Promena statusa nije dozvoljena";
    public const string InvalidStatusTransitionDetail = "Porudžbina ne može da pređe iz trenutnog u traženi status.";

    public const string OrderNotDeliveredTitle = "Porudžbina nije isporučena";
    public const string OrderNotDeliveredDetail = "Naplata se evidentira samo za isporučene porudžbine.";

    public const string CourierNameTakenTitle = "Naziv je zauzet";
    public const string CourierNameTakenDetail = "Kurirska služba sa ovim nazivom već postoji.";
}
