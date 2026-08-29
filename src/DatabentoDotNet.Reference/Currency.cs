using System.Collections.Frozen;
using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// A currency code — ISO 4217 alpha-3.
/// </summary>
/// <remarks>
/// <para>
/// <b>An open set: a code this library does not know is carried, not lost.</b> Upstream ends
/// this enum in an <c>Unknown(String)</c> variant (<c>enums.rs:1169</c>) so a code Databento adds
/// next month round-trips untouched, and a C# <c>enum</c> cannot hold a payload. See
/// <see cref="IReferenceCode{TSelf}"/> for the shape this takes instead and why.
/// </para>
/// <para>
/// The members come from the <c>CUREN</c> group of the vendored <c>corporate_actions.list_enums</c> response, which is the oracle rather than a count typed into an issue.
/// </para>
/// </remarks>
[JsonConverter(typeof(ReferenceCodeJsonConverter<Currency>))]
public readonly record struct Currency : IReferenceCode<Currency>
{
    private static readonly FrozenSet<string> Codes = FrozenSet.ToFrozenSet(
    [
        "AED",
        "AFN",
        "ALL",
        "AMD",
        "ANG",
        "AOA",
        "ARS",
        "AUD",
        "AWG",
        "AZN",
        "BAM",
        "BBD",
        "BDT",
        "BGN",
        "BHD",
        "BIF",
        "BMD",
        "BND",
        "BOB",
        "BOV",
        "BRL",
        "BSD",
        "BTN",
        "BWP",
        "BYN",
        "BZD",
        "CAD",
        "CDF",
        "CHF",
        "CLF",
        "CLP",
        "CNY",
        "COP",
        "COU",
        "CRC",
        "CUP",
        "CVE",
        "CYP",
        "CZK",
        "DJF",
        "DKK",
        "DOP",
        "DZD",
        "ECS",
        "EEK",
        "EGP",
        "ERN",
        "ETB",
        "EUR",
        "FJD",
        "FKP",
        "GBP",
        "GBX",
        "GEL",
        "GHS",
        "GIP",
        "GMD",
        "GNF",
        "GRD",
        "GTQ",
        "GYD",
        "HKD",
        "HNL",
        "HRK",
        "HTG",
        "HUF",
        "IDR",
        "ILS",
        "INR",
        "IQD",
        "IRR",
        "ISK",
        "JMD",
        "JOD",
        "JPY",
        "KES",
        "KGS",
        "KHR",
        "KMF",
        "KPW",
        "KRW",
        "KWD",
        "KYD",
        "KZT",
        "LAK",
        "LBP",
        "LKR",
        "LRD",
        "LSL",
        "LTL",
        "LYD",
        "MAD",
        "MDL",
        "MGA",
        "MKD",
        "MMK",
        "MNT",
        "MOP",
        "MRO",
        "MUR",
        "MVR",
        "MWK",
        "MXN",
        "MXV",
        "MYR",
        "MZN",
        "NAD",
        "NGN",
        "NIO",
        "NOK",
        "NPR",
        "NZD",
        "OMR",
        "PAB",
        "PEN",
        "PGK",
        "PHP",
        "PKR",
        "PLN",
        "PYG",
        "QAR",
        "RON",
        "RSD",
        "RUB",
        "RWF",
        "SAR",
        "SBD",
        "SCR",
        "SDD",
        "SDG",
        "SEK",
        "SGD",
        "SHP",
        "SLL",
        "SOS",
        "SRD",
        "STD",
        "SVC",
        "SYP",
        "SZL",
        "THB",
        "TJS",
        "TMM",
        "TND",
        "TOP",
        "TRY",
        "TTD",
        "TWD",
        "TZS",
        "UAH",
        "UGX",
        "USD",
        "USX",
        "UYI",
        "UYU",
        "UYW",
        "UZS",
        "VEF",
        "VES",
        "VND",
        "VUV",
        "WST",
        "XAF",
        "XCD",
        "XDR",
        "XFU",
        "XOF",
        "XPF",
        "XTS",
        "XXX",
        "YER",
        "ZAC",
        "ZAR",
        "ZMK",
        "ZMW",
        "ZRN",
        "ZWD",
        "ZWG",
        "ZWL",
    ], StringComparer.Ordinal);

    private readonly string? _code;

    /// <summary>
    /// Wraps a wire code, known or not. Prefer a named member such as
    /// <see cref="Aed"/> where one exists, and <see cref="From"/> where the value came
    /// from the server.
    /// </summary>
    /// <param name="code">The wire code.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null or empty. A blank code is the absence of a value, which is <see langword="default"/>.</exception>
    public Currency(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        _code = code;
    }

    /// <summary>
    /// Every code the reference API reported for this type when the fixture was captured —
    /// 179 of them.
    /// </summary>
    public static IReadOnlySet<string> KnownCodes => Codes;

    /// <inheritdoc/>
    public string? Code => _code;

    /// <inheritdoc/>
    public bool HasValue => _code is not null;

    /// <inheritdoc/>
    public bool IsKnown => _code is not null && Codes.Contains(_code);

    /// <summary>
    /// Reads a wire code, mapping <see langword="null"/> and the empty string to
    /// <see langword="default"/> — the absence of a value.
    /// </summary>
    /// <param name="code">The wire code, or <see langword="null"/>.</param>
    /// <returns>The value.</returns>
    public static Currency From(string? code) => string.IsNullOrEmpty(code) ? default : new(code);

    /// <summary>The wire code, or the empty string when this names no value.</summary>
    /// <returns>The wire code.</returns>
    public override string ToString() => _code ?? string.Empty;

    /// <summary>UAE Dirham (<c>AED</c>).</summary>
    public static Currency Aed => new("AED");

    /// <summary>Afghanis (<c>AFN</c>).</summary>
    public static Currency Afn => new("AFN");

    /// <summary>Albanian Lek (<c>ALL</c>).</summary>
    public static Currency All => new("ALL");

    /// <summary>Armenian Dram (<c>AMD</c>).</summary>
    public static Currency Amd => new("AMD");

    /// <summary>Netherlands Antilles Guilders (<c>ANG</c>).</summary>
    public static Currency Ang => new("ANG");

    /// <summary>Angola Kwanza (<c>AOA</c>).</summary>
    public static Currency Aoa => new("AOA");

    /// <summary>Argentine Peso (<c>ARS</c>).</summary>
    public static Currency Ars => new("ARS");

    /// <summary>Australian Dollar (<c>AUD</c>).</summary>
    public static Currency Aud => new("AUD");

    /// <summary>Aruban Guilder (<c>AWG</c>).</summary>
    public static Currency Awg => new("AWG");

    /// <summary>Azerbaijani Manat (<c>AZN</c>).</summary>
    public static Currency Azn => new("AZN");

    /// <summary>Convertible Marks (<c>BAM</c>).</summary>
    public static Currency Bam => new("BAM");

    /// <summary>Barbados Dollar (<c>BBD</c>).</summary>
    public static Currency Bbd => new("BBD");

    /// <summary>Bangladesh Taka (<c>BDT</c>).</summary>
    public static Currency Bdt => new("BDT");

    /// <summary>Bulgarian Lev (<c>BGN</c>).</summary>
    public static Currency Bgn => new("BGN");

    /// <summary>Bahraini Dinar (<c>BHD</c>).</summary>
    public static Currency Bhd => new("BHD");

    /// <summary>Burundi Franc (<c>BIF</c>).</summary>
    public static Currency Bif => new("BIF");

    /// <summary>Bermuda Dollar (<c>BMD</c>).</summary>
    public static Currency Bmd => new("BMD");

    /// <summary>Brunei Dollar (<c>BND</c>).</summary>
    public static Currency Bnd => new("BND");

    /// <summary>Boliviano (<c>BOB</c>).</summary>
    public static Currency Bob => new("BOB");

    /// <summary>Mvdol (<c>BOV</c>).</summary>
    public static Currency Bov => new("BOV");

    /// <summary>Brazilian Real (<c>BRL</c>).</summary>
    public static Currency Brl => new("BRL");

    /// <summary>Bahamas Dollar (<c>BSD</c>).</summary>
    public static Currency Bsd => new("BSD");

    /// <summary>Bhutanese Ngultrum (<c>BTN</c>).</summary>
    public static Currency Btn => new("BTN");

    /// <summary>Botswana Pula (<c>BWP</c>).</summary>
    public static Currency Bwp => new("BWP");

    /// <summary>Belarusian Ruble (New) (<c>BYN</c>).</summary>
    public static Currency Byn => new("BYN");

    /// <summary>Belize Dollar (<c>BZD</c>).</summary>
    public static Currency Bzd => new("BZD");

    /// <summary>Canadian Dollar (<c>CAD</c>).</summary>
    public static Currency Cad => new("CAD");

    /// <summary>Congolese Franc (<c>CDF</c>).</summary>
    public static Currency Cdf => new("CDF");

    /// <summary>Swiss Francs (<c>CHF</c>).</summary>
    public static Currency Chf => new("CHF");

    /// <summary>Chilean Unidad de Fomento (<c>CLF</c>).</summary>
    public static Currency Clf => new("CLF");

    /// <summary>Chilean Peso (<c>CLP</c>).</summary>
    public static Currency Clp => new("CLP");

    /// <summary>Chinese Yuan Renminbi (<c>CNY</c>).</summary>
    public static Currency Cny => new("CNY");

    /// <summary>Colombian Peso (<c>COP</c>).</summary>
    public static Currency Cop => new("COP");

    /// <summary>Colombian (Unidad de Valor Real) (<c>COU</c>).</summary>
    public static Currency Cou => new("COU");

    /// <summary>Costa Rican Colon (<c>CRC</c>).</summary>
    public static Currency Crc => new("CRC");

    /// <summary>Cuban Peso (<c>CUP</c>).</summary>
    public static Currency Cup => new("CUP");

    /// <summary>Cape Verde Escudo (<c>CVE</c>).</summary>
    public static Currency Cve => new("CVE");

    /// <summary>Cypriot Pound (<c>CYP</c>).</summary>
    public static Currency Cyp => new("CYP");

    /// <summary>Czech Koruna (<c>CZK</c>).</summary>
    public static Currency Czk => new("CZK");

    /// <summary>Djibouti Franc (<c>DJF</c>).</summary>
    public static Currency Djf => new("DJF");

    /// <summary>Danish Kroner (<c>DKK</c>).</summary>
    public static Currency Dkk => new("DKK");

    /// <summary>Dominican Peso (<c>DOP</c>).</summary>
    public static Currency Dop => new("DOP");

    /// <summary>Algerian Dinar (<c>DZD</c>).</summary>
    public static Currency Dzd => new("DZD");

    /// <summary>Ecuador Sucre (<c>ECS</c>).</summary>
    public static Currency Ecs => new("ECS");

    /// <summary>Estonian Kroon (<c>EEK</c>).</summary>
    public static Currency Eek => new("EEK");

    /// <summary>Egyptian Pound (<c>EGP</c>).</summary>
    public static Currency Egp => new("EGP");

    /// <summary>Eritrean Nakfa (<c>ERN</c>).</summary>
    public static Currency Ern => new("ERN");

    /// <summary>Ethiopian Birr (<c>ETB</c>).</summary>
    public static Currency Etb => new("ETB");

    /// <summary>Euros (<c>EUR</c>).</summary>
    public static Currency Eur => new("EUR");

    /// <summary>Fiji Dollar (<c>FJD</c>).</summary>
    public static Currency Fjd => new("FJD");

    /// <summary>Falklands Pounds (<c>FKP</c>).</summary>
    public static Currency Fkp => new("FKP");

    /// <summary>Pound Sterling (<c>GBP</c>).</summary>
    public static Currency Gbp => new("GBP");

    /// <summary>GB Pence (<c>GBX</c>).</summary>
    public static Currency Gbx => new("GBX");

    /// <summary>Georgian Lari (<c>GEL</c>).</summary>
    public static Currency Gel => new("GEL");

    /// <summary>Ghanaian Cedi (<c>GHS</c>).</summary>
    public static Currency Ghs => new("GHS");

    /// <summary>Gibraltar Pounds (<c>GIP</c>).</summary>
    public static Currency Gip => new("GIP");

    /// <summary>Gambian Dalasi (<c>GMD</c>).</summary>
    public static Currency Gmd => new("GMD");

    /// <summary>Guinean Franc (<c>GNF</c>).</summary>
    public static Currency Gnf => new("GNF");

    /// <summary>Greek Drachma (<c>GRD</c>).</summary>
    public static Currency Grd => new("GRD");

    /// <summary>Guatamala Quetzal (<c>GTQ</c>).</summary>
    public static Currency Gtq => new("GTQ");

    /// <summary>Guyana Dollar (<c>GYD</c>).</summary>
    public static Currency Gyd => new("GYD");

    /// <summary>Hong Kong Dollar (<c>HKD</c>).</summary>
    public static Currency Hkd => new("HKD");

    /// <summary>Honduras Lempira (<c>HNL</c>).</summary>
    public static Currency Hnl => new("HNL");

    /// <summary>Croatian Kuna (<c>HRK</c>).</summary>
    public static Currency Hrk => new("HRK");

    /// <summary>Haiti Gourde (<c>HTG</c>).</summary>
    public static Currency Htg => new("HTG");

    /// <summary>Hungarian Forint (<c>HUF</c>).</summary>
    public static Currency Huf => new("HUF");

    /// <summary>Indonesian Rupiah (<c>IDR</c>).</summary>
    public static Currency Idr => new("IDR");

    /// <summary>Israeli New Shekel (<c>ILS</c>).</summary>
    public static Currency Ils => new("ILS");

    /// <summary>Indian Rupees (<c>INR</c>).</summary>
    public static Currency Inr => new("INR");

    /// <summary>Iraqi Dinar (<c>IQD</c>).</summary>
    public static Currency Iqd => new("IQD");

    /// <summary>Iranian Rial (<c>IRR</c>).</summary>
    public static Currency Irr => new("IRR");

    /// <summary>Icelandic Krona (<c>ISK</c>).</summary>
    public static Currency Isk => new("ISK");

    /// <summary>Jamaican Dollar (<c>JMD</c>).</summary>
    public static Currency Jmd => new("JMD");

    /// <summary>Jordanian Dinar (<c>JOD</c>).</summary>
    public static Currency Jod => new("JOD");

    /// <summary>Japanese Yen (<c>JPY</c>).</summary>
    public static Currency Jpy => new("JPY");

    /// <summary>Kenyan Shilling (<c>KES</c>).</summary>
    public static Currency Kes => new("KES");

    /// <summary>Kyrgyzstan Som (<c>KGS</c>).</summary>
    public static Currency Kgs => new("KGS");

    /// <summary>Cambodian Riel (<c>KHR</c>).</summary>
    public static Currency Khr => new("KHR");

    /// <summary>Comoro Franc (<c>KMF</c>).</summary>
    public static Currency Kmf => new("KMF");

    /// <summary>North Korean Won (<c>KPW</c>).</summary>
    public static Currency Kpw => new("KPW");

    /// <summary>Korean Won (<c>KRW</c>).</summary>
    public static Currency Krw => new("KRW");

    /// <summary>Kuwaiti Dinar (<c>KWD</c>).</summary>
    public static Currency Kwd => new("KWD");

    /// <summary>Cayman Islands Dollar (<c>KYD</c>).</summary>
    public static Currency Kyd => new("KYD");

    /// <summary>Kazakhstan Tenge (<c>KZT</c>).</summary>
    public static Currency Kzt => new("KZT");

    /// <summary>Lao Liberation Kip (<c>LAK</c>).</summary>
    public static Currency Lak => new("LAK");

    /// <summary>Lebanese Pound (<c>LBP</c>).</summary>
    public static Currency Lbp => new("LBP");

    /// <summary>Sri Lankan Rupee (<c>LKR</c>).</summary>
    public static Currency Lkr => new("LKR");

    /// <summary>Liberian Dollar (<c>LRD</c>).</summary>
    public static Currency Lrd => new("LRD");

    /// <summary>Lesotho Loti (<c>LSL</c>).</summary>
    public static Currency Lsl => new("LSL");

    /// <summary>Lithuanian Litas (<c>LTL</c>).</summary>
    public static Currency Ltl => new("LTL");

    /// <summary>Libyan Dinar (<c>LYD</c>).</summary>
    public static Currency Lyd => new("LYD");

    /// <summary>Moroccan Dirham (<c>MAD</c>).</summary>
    public static Currency Mad => new("MAD");

    /// <summary>Moldovan Leu (<c>MDL</c>).</summary>
    public static Currency Mdl => new("MDL");

    /// <summary>Malagasy Ariary (<c>MGA</c>).</summary>
    public static Currency Mga => new("MGA");

    /// <summary>Macedonian Denar (<c>MKD</c>).</summary>
    public static Currency Mkd => new("MKD");

    /// <summary>Myanmar Kyat (<c>MMK</c>).</summary>
    public static Currency Mmk => new("MMK");

    /// <summary>Mongolian Tugrik (<c>MNT</c>).</summary>
    public static Currency Mnt => new("MNT");

    /// <summary>Macau Pataca (<c>MOP</c>).</summary>
    public static Currency Mop => new("MOP");

    /// <summary>Mauritanian Ouguiya (<c>MRO</c>).</summary>
    public static Currency Mro => new("MRO");

    /// <summary>Mauritius Rupee (<c>MUR</c>).</summary>
    public static Currency Mur => new("MUR");

    /// <summary>Maldivian Rufiyaa (<c>MVR</c>).</summary>
    public static Currency Mvr => new("MVR");

    /// <summary>Malawi Kwacha (<c>MWK</c>).</summary>
    public static Currency Mwk => new("MWK");

    /// <summary>Mexican Nuevo Peso (<c>MXN</c>).</summary>
    public static Currency Mxn => new("MXN");

    /// <summary>Mexican Unidad de Inversion (UDI) (<c>MXV</c>).</summary>
    public static Currency Mxv => new("MXV");

    /// <summary>Malaysian Ringgit (<c>MYR</c>).</summary>
    public static Currency Myr => new("MYR");

    /// <summary>Mozambique Metical (<c>MZN</c>).</summary>
    public static Currency Mzn => new("MZN");

    /// <summary>Namibian Dollar (<c>NAD</c>).</summary>
    public static Currency Nad => new("NAD");

    /// <summary>Nigerian Naira (<c>NGN</c>).</summary>
    public static Currency Ngn => new("NGN");

    /// <summary>Nicaraguan Cordoba Oro (<c>NIO</c>).</summary>
    public static Currency Nio => new("NIO");

    /// <summary>Norwegian Krone (<c>NOK</c>).</summary>
    public static Currency Nok => new("NOK");

    /// <summary>Nepalese Rupee (<c>NPR</c>).</summary>
    public static Currency Npr => new("NPR");

    /// <summary>New Zealand Dollar (<c>NZD</c>).</summary>
    public static Currency Nzd => new("NZD");

    /// <summary>Omani Rial (<c>OMR</c>).</summary>
    public static Currency Omr => new("OMR");

    /// <summary>Panama Balboa (<c>PAB</c>).</summary>
    public static Currency Pab => new("PAB");

    /// <summary>Peruvian Nuevo Sol (<c>PEN</c>).</summary>
    public static Currency Pen => new("PEN");

    /// <summary>Papua New Guinea Kina (<c>PGK</c>).</summary>
    public static Currency Pgk => new("PGK");

    /// <summary>Philippines Peso (<c>PHP</c>).</summary>
    public static Currency Php => new("PHP");

    /// <summary>Pakistan Rupee (<c>PKR</c>).</summary>
    public static Currency Pkr => new("PKR");

    /// <summary>Polish Złoty (New) (<c>PLN</c>).</summary>
    public static Currency Pln => new("PLN");

    /// <summary>Paraguay Guarani (<c>PYG</c>).</summary>
    public static Currency Pyg => new("PYG");

    /// <summary>Qatar Rial (<c>QAR</c>).</summary>
    public static Currency Qar => new("QAR");

    /// <summary>Romanian Leu (New) (<c>RON</c>).</summary>
    public static Currency Ron => new("RON");

    /// <summary>Serbian Dinars (<c>RSD</c>).</summary>
    public static Currency Rsd => new("RSD");

    /// <summary>Russian Ruble (New) (<c>RUB</c>).</summary>
    public static Currency Rub => new("RUB");

    /// <summary>Rwandan Franc (<c>RWF</c>).</summary>
    public static Currency Rwf => new("RWF");

    /// <summary>Saudi Arabian Riyal (<c>SAR</c>).</summary>
    public static Currency Sar => new("SAR");

    /// <summary>Solomon Islands Dollar (<c>SBD</c>).</summary>
    public static Currency Sbd => new("SBD");

    /// <summary>Seychelles Rupee (<c>SCR</c>).</summary>
    public static Currency Scr => new("SCR");

    /// <summary>Sudanese Dinar (<c>SDD</c>).</summary>
    public static Currency Sdd => new("SDD");

    /// <summary>Sudanese Pound (<c>SDG</c>).</summary>
    public static Currency Sdg => new("SDG");

    /// <summary>Swedish Kroner (<c>SEK</c>).</summary>
    public static Currency Sek => new("SEK");

    /// <summary>Singapore Dollar (<c>SGD</c>).</summary>
    public static Currency Sgd => new("SGD");

    /// <summary>St. Helena Pounds (<c>SHP</c>).</summary>
    public static Currency Shp => new("SHP");

    /// <summary>Sierra Leone (<c>SLL</c>).</summary>
    public static Currency Sll => new("SLL");

    /// <summary>Somalia Shilling (<c>SOS</c>).</summary>
    public static Currency Sos => new("SOS");

    /// <summary>Surinam Dollar (<c>SRD</c>).</summary>
    public static Currency Srd => new("SRD");

    /// <summary>Sao Tome and Principe Dobra (<c>STD</c>).</summary>
    public static Currency Std => new("STD");

    /// <summary>El Salvador Colon (<c>SVC</c>).</summary>
    public static Currency Svc => new("SVC");

    /// <summary>Syrian Pound (<c>SYP</c>).</summary>
    public static Currency Syp => new("SYP");

    /// <summary>Swaziland Lilangeni (<c>SZL</c>).</summary>
    public static Currency Szl => new("SZL");

    /// <summary>Thai Baht (<c>THB</c>).</summary>
    public static Currency Thb => new("THB");

    /// <summary>Tajikistani Somoni (<c>TJS</c>).</summary>
    public static Currency Tjs => new("TJS");

    /// <summary>Turkmenistan Manat (<c>TMM</c>).</summary>
    public static Currency Tmm => new("TMM");

    /// <summary>Tunisian Dinar (<c>TND</c>).</summary>
    public static Currency Tnd => new("TND");

    /// <summary>Tonga Pa`anga (<c>TOP</c>).</summary>
    public static Currency Top => new("TOP");

    /// <summary>Turkish Lira (New) (<c>TRY</c>).</summary>
    public static Currency Try => new("TRY");

    /// <summary>Trinidad and Tobago Dollar (<c>TTD</c>).</summary>
    public static Currency Ttd => new("TTD");

    /// <summary>Taiwan Dollar (<c>TWD</c>).</summary>
    public static Currency Twd => new("TWD");

    /// <summary>Tanzanian Shilling (<c>TZS</c>).</summary>
    public static Currency Tzs => new("TZS");

    /// <summary>Ukrainian Hryvnia (<c>UAH</c>).</summary>
    public static Currency Uah => new("UAH");

    /// <summary>Ugandan Shilling (<c>UGX</c>).</summary>
    public static Currency Ugx => new("UGX");

    /// <summary>US Dollar (<c>USD</c>).</summary>
    public static Currency Usd => new("USD");

    /// <summary>US Cents (<c>USX</c>).</summary>
    public static Currency Usx => new("USX");

    /// <summary>Uruguay Peso (Index Linked) (<c>UYI</c>).</summary>
    public static Currency Uyi => new("UYI");

    /// <summary>Uruguayan Peso (<c>UYU</c>).</summary>
    public static Currency Uyu => new("UYU");

    /// <summary>Uruguayan Unidad Previsional (Pension Unit) (<c>UYW</c>).</summary>
    public static Currency Uyw => new("UYW");

    /// <summary>Uzbekistan Sum (<c>UZS</c>).</summary>
    public static Currency Uzs => new("UZS");

    /// <summary>Venezuala Bolivares Fuertes (<c>VEF</c>).</summary>
    public static Currency Vef => new("VEF");

    /// <summary>Venezuela Sovereign Bolivar (<c>VES</c>).</summary>
    public static Currency Ves => new("VES");

    /// <summary>Vietnamese Dong (<c>VND</c>).</summary>
    public static Currency Vnd => new("VND");

    /// <summary>Vanuatu Vatu (<c>VUV</c>).</summary>
    public static Currency Vuv => new("VUV");

    /// <summary>Samoan Tala (<c>WST</c>).</summary>
    public static Currency Wst => new("WST");

    /// <summary>CFA Franc (BEAC) (<c>XAF</c>).</summary>
    public static Currency Xaf => new("XAF");

    /// <summary>Caribbean Dollar (<c>XCD</c>).</summary>
    public static Currency Xcd => new("XCD");

    /// <summary>International Monetary Fund (<c>XDR</c>).</summary>
    public static Currency Xdr => new("XDR");

    /// <summary>UIC-Franc (<c>XFU</c>).</summary>
    public static Currency Xfu => new("XFU");

    /// <summary>CFA Franc (BCEAO) (<c>XOF</c>).</summary>
    public static Currency Xof => new("XOF");

    /// <summary>CFP Franc (<c>XPF</c>).</summary>
    public static Currency Xpf => new("XPF");

    /// <summary>Codes for testing purposes (<c>XTS</c>).</summary>
    public static Currency Xts => new("XTS");

    /// <summary>Codes for transactions/no currencies involved (<c>XXX</c>).</summary>
    public static Currency Xxx => new("XXX");

    /// <summary>North Yemen Rial (<c>YER</c>).</summary>
    public static Currency Yer => new("YER");

    /// <summary>South African Cents (<c>ZAC</c>).</summary>
    public static Currency Zac => new("ZAC");

    /// <summary>South African Rand (<c>ZAR</c>).</summary>
    public static Currency Zar => new("ZAR");

    /// <summary>Zambian Kwacha (<c>ZMK</c>).</summary>
    public static Currency Zmk => new("ZMK");

    /// <summary>Zambian New Kwacha (<c>ZMW</c>).</summary>
    public static Currency Zmw => new("ZMW");

    /// <summary>New Zaire (<c>ZRN</c>).</summary>
    public static Currency Zrn => new("ZRN");

    /// <summary>Zimbabwe Dollar (<c>ZWD</c>).</summary>
    public static Currency Zwd => new("ZWD");

    /// <summary>Zimbabwe Gold (<c>ZWG</c>).</summary>
    public static Currency Zwg => new("ZWG");

    /// <summary>Zimbabwean Dollar (<c>ZWL</c>).</summary>
    public static Currency Zwl => new("ZWL");
}
