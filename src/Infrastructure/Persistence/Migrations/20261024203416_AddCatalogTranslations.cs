using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogTranslations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogTranslations",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogTranslations", x => new { x.Kind, x.EntityId, x.Locale });
                });

            migrationBuilder.InsertData(
                table: "CatalogTranslations",
                columns: new[] { "EntityId", "Kind", "Locale", "Name" },
                values: new object[,]
                {
                    { new Guid("00b577aa-2127-533c-8a68-f9099001f88a"), "ingredient", "en", "Cardamom" },
                    { new Guid("00b577aa-2127-533c-8a68-f9099001f88a"), "ingredient", "nb", "Kardemomme" },
                    { new Guid("018b5367-aabf-5218-9a80-387c1eefa30a"), "ingredient", "en", "Coconut milk" },
                    { new Guid("018b5367-aabf-5218-9a80-387c1eefa30a"), "ingredient", "nb", "Kokosmelk" },
                    { new Guid("05a6160e-87cd-5d2c-81df-c0a753ba5e19"), "ingredient", "en", "Sour cream" },
                    { new Guid("05a6160e-87cd-5d2c-81df-c0a753ba5e19"), "ingredient", "nb", "Rømme" },
                    { new Guid("062621ee-301b-53d1-aa7b-0e00f1c33081"), "ingredient", "en", "Celery" },
                    { new Guid("062621ee-301b-53d1-aa7b-0e00f1c33081"), "ingredient", "nb", "Selleri" },
                    { new Guid("079d2db4-8e75-543b-84fe-5c82a7963559"), "ingredient", "en", "Ham" },
                    { new Guid("079d2db4-8e75-543b-84fe-5c82a7963559"), "ingredient", "nb", "Skinke" },
                    { new Guid("0942b134-c6e7-5b24-b9c7-0aa52f8783d6"), "ingredient", "en", "Spring onion" },
                    { new Guid("0942b134-c6e7-5b24-b9c7-0aa52f8783d6"), "ingredient", "nb", "Vårløk" },
                    { new Guid("0c6bbe3a-b113-52a5-9fbe-c680dc6fa614"), "ingredient", "en", "Sausages" },
                    { new Guid("0c6bbe3a-b113-52a5-9fbe-c680dc6fa614"), "ingredient", "nb", "Pølser" },
                    { new Guid("0d7adfbb-5b54-5ae4-bd7e-ab5ea9de6b86"), "ingredient", "en", "Apple" },
                    { new Guid("0d7adfbb-5b54-5ae4-bd7e-ab5ea9de6b86"), "ingredient", "nb", "Eple" },
                    { new Guid("0eb2fb2b-c95a-5044-99a3-d9453c52cc2d"), "ingredient", "en", "Cornstarch" },
                    { new Guid("0eb2fb2b-c95a-5044-99a3-d9453c52cc2d"), "ingredient", "nb", "Maisenna" },
                    { new Guid("0ede68df-ce20-58e2-a9fa-6cbeadf05b8b"), "ingredient", "en", "Tortilla wraps" },
                    { new Guid("0ede68df-ce20-58e2-a9fa-6cbeadf05b8b"), "ingredient", "nb", "Tortillalefser" },
                    { new Guid("1105b71c-7f8b-537d-a00c-11f2d21798a2"), "ingredient", "en", "Brown sugar" },
                    { new Guid("1105b71c-7f8b-537d-a00c-11f2d21798a2"), "ingredient", "nb", "Brunt sukker" },
                    { new Guid("1127b196-6678-52fb-afe8-80ffd554e70c"), "ingredient", "en", "Chicken breast fillet" },
                    { new Guid("1127b196-6678-52fb-afe8-80ffd554e70c"), "ingredient", "nb", "Kyllingfilet" },
                    { new Guid("12aaac43-57fb-55c3-979c-3a7cd2c71f22"), "ingredient", "en", "Eggplant" },
                    { new Guid("12aaac43-57fb-55c3-979c-3a7cd2c71f22"), "ingredient", "nb", "Aubergine" },
                    { new Guid("12e6be7c-2862-509e-b602-b5bac890ca06"), "ingredient", "en", "Wheat flour" },
                    { new Guid("12e6be7c-2862-509e-b602-b5bac890ca06"), "ingredient", "nb", "Hvetemel" },
                    { new Guid("14edc321-5475-52f2-9b9c-a8e58631d691"), "ingredient", "en", "Peas" },
                    { new Guid("14edc321-5475-52f2-9b9c-a8e58631d691"), "ingredient", "nb", "Erter" },
                    { new Guid("16bb8de3-129a-5b67-867e-969a2270c5e5"), "ingredient", "en", "Strawberries" },
                    { new Guid("16bb8de3-129a-5b67-867e-969a2270c5e5"), "ingredient", "nb", "Jordbær" },
                    { new Guid("182bfd36-b47f-51af-b97d-c9c5e870636a"), "ingredient", "en", "Canned tomatoes" },
                    { new Guid("182bfd36-b47f-51af-b97d-c9c5e870636a"), "ingredient", "nb", "Hermetiske tomater" },
                    { new Guid("18aeb9c6-f737-5ae7-a050-a69952a33d65"), "ingredient", "en", "Rolled oats" },
                    { new Guid("18aeb9c6-f737-5ae7-a050-a69952a33d65"), "ingredient", "nb", "Havregryn" },
                    { new Guid("1e775c0f-11de-56e3-9e48-162a8ce306b5"), "ingredient", "en", "Bay leaf" },
                    { new Guid("1e775c0f-11de-56e3-9e48-162a8ce306b5"), "ingredient", "nb", "Laurbærblad" },
                    { new Guid("22fdd055-6217-5f93-93a4-e2c4fad857cb"), "ingredient", "en", "Soy sauce" },
                    { new Guid("22fdd055-6217-5f93-93a4-e2c4fad857cb"), "ingredient", "nb", "Soyasaus" },
                    { new Guid("2437ea85-570e-5944-8af8-0a9572fae257"), "ingredient", "en", "Basil" },
                    { new Guid("2437ea85-570e-5944-8af8-0a9572fae257"), "ingredient", "nb", "Basilikum" },
                    { new Guid("25530a97-c780-5914-b22c-c281940b37e1"), "ingredient", "en", "Tomato puree" },
                    { new Guid("25530a97-c780-5914-b22c-c281940b37e1"), "ingredient", "nb", "Tomatpuré" },
                    { new Guid("28317d98-d5d6-521a-b011-a35b77fa4f55"), "ingredient", "en", "Couscous" },
                    { new Guid("28317d98-d5d6-521a-b011-a35b77fa4f55"), "ingredient", "nb", "Couscous" },
                    { new Guid("28f29641-7731-5158-ad85-90ace26007a6"), "ingredient", "en", "Cauliflower" },
                    { new Guid("28f29641-7731-5158-ad85-90ace26007a6"), "ingredient", "nb", "Blomkål" },
                    { new Guid("2c102b4b-c557-59f1-ba38-fbb7edef5a2b"), "ingredient", "en", "Fish fingers" },
                    { new Guid("2c102b4b-c557-59f1-ba38-fbb7edef5a2b"), "ingredient", "nb", "Fiskepinner" },
                    { new Guid("2da824bf-7d71-5906-8fd3-a2a54e547c69"), "ingredient", "en", "Mozzarella" },
                    { new Guid("2da824bf-7d71-5906-8fd3-a2a54e547c69"), "ingredient", "nb", "Mozzarella" },
                    { new Guid("2f80230b-4d31-56e2-b12f-f79776e21680"), "ingredient", "en", "Pork chop" },
                    { new Guid("2f80230b-4d31-56e2-b12f-f79776e21680"), "ingredient", "nb", "Svinekotelett" },
                    { new Guid("302edd54-59dc-5145-b469-c988cf1f3694"), "ingredient", "en", "Red onion" },
                    { new Guid("302edd54-59dc-5145-b469-c988cf1f3694"), "ingredient", "nb", "Rødløk" },
                    { new Guid("340165f7-81cb-5452-8e49-a5a4e02d3962"), "ingredient", "en", "Minced meat" },
                    { new Guid("340165f7-81cb-5452-8e49-a5a4e02d3962"), "ingredient", "nb", "Kjøttdeig" },
                    { new Guid("358bbaa7-68e5-5ebb-8bd7-adaa02707a1f"), "ingredient", "en", "Peanut butter" },
                    { new Guid("358bbaa7-68e5-5ebb-8bd7-adaa02707a1f"), "ingredient", "nb", "Peanøttsmør" },
                    { new Guid("38796544-559e-5b16-833c-65eae0c8be7a"), "ingredient", "en", "Spaghetti" },
                    { new Guid("38796544-559e-5b16-833c-65eae0c8be7a"), "ingredient", "nb", "Spaghetti" },
                    { new Guid("3cb77c8d-983a-5eef-a4fe-ac08cb08309b"), "ingredient", "en", "Chili" },
                    { new Guid("3cb77c8d-983a-5eef-a4fe-ac08cb08309b"), "ingredient", "nb", "Chili" },
                    { new Guid("481a68a8-b863-58be-87c5-0f2860de7f0e"), "ingredient", "en", "Lemon" },
                    { new Guid("481a68a8-b863-58be-87c5-0f2860de7f0e"), "ingredient", "nb", "Sitron" },
                    { new Guid("48f5afed-5b6d-5e25-af57-48a47694d3f7"), "ingredient", "en", "Mustard" },
                    { new Guid("48f5afed-5b6d-5e25-af57-48a47694d3f7"), "ingredient", "nb", "Sennep" },
                    { new Guid("4cc2ada6-73b1-506f-818f-c5855d935d73"), "ingredient", "en", "Parsley" },
                    { new Guid("4cc2ada6-73b1-506f-818f-c5855d935d73"), "ingredient", "nb", "Persille" },
                    { new Guid("4e968abd-1dfe-579d-acf9-de5fdfeae671"), "ingredient", "en", "Vinegar" },
                    { new Guid("4e968abd-1dfe-579d-acf9-de5fdfeae671"), "ingredient", "nb", "Eddik" },
                    { new Guid("55d1d54c-4791-500c-947d-11264953c044"), "ingredient", "en", "Bacon" },
                    { new Guid("55d1d54c-4791-500c-947d-11264953c044"), "ingredient", "nb", "Bacon" },
                    { new Guid("578d165a-9764-56e3-8eee-7979def50aef"), "ingredient", "en", "Carrot" },
                    { new Guid("578d165a-9764-56e3-8eee-7979def50aef"), "ingredient", "nb", "Gulrot" },
                    { new Guid("5a1af167-140c-561c-a977-8aefd9618c68"), "ingredient", "en", "Noodles" },
                    { new Guid("5a1af167-140c-561c-a977-8aefd9618c68"), "ingredient", "nb", "Nudler" },
                    { new Guid("5a4a9fbe-71e7-55a1-bd65-e5a626e66c51"), "ingredient", "en", "Orange" },
                    { new Guid("5a4a9fbe-71e7-55a1-bd65-e5a626e66c51"), "ingredient", "nb", "Appelsin" },
                    { new Guid("60cfdd20-06dd-509e-91d6-df0929a098c7"), "ingredient", "en", "Blueberries" },
                    { new Guid("60cfdd20-06dd-509e-91d6-df0929a098c7"), "ingredient", "nb", "Blåbær" },
                    { new Guid("62629f8f-0cb8-5245-a091-6a7fe95b914b"), "ingredient", "en", "Stock cube" },
                    { new Guid("62629f8f-0cb8-5245-a091-6a7fe95b914b"), "ingredient", "nb", "Buljongterning" },
                    { new Guid("6593ae76-1a40-50b7-b118-976d50dd35a1"), "ingredient", "en", "Egg" },
                    { new Guid("6593ae76-1a40-50b7-b118-976d50dd35a1"), "ingredient", "nb", "Egg" },
                    { new Guid("6871d5c3-f983-5046-870b-095b26f4c9b3"), "ingredient", "en", "Plain yoghurt" },
                    { new Guid("6871d5c3-f983-5046-870b-095b26f4c9b3"), "ingredient", "nb", "Yoghurt naturell" },
                    { new Guid("6968fff3-8d67-5bca-ac32-0caea672f02b"), "ingredient", "en", "Shrimp" },
                    { new Guid("6968fff3-8d67-5bca-ac32-0caea672f02b"), "ingredient", "nb", "Reker" },
                    { new Guid("6aca22ad-6eff-588f-b605-fb3d2c81df89"), "ingredient", "en", "Potato" },
                    { new Guid("6aca22ad-6eff-588f-b605-fb3d2c81df89"), "ingredient", "nb", "Potet" },
                    { new Guid("6bbf5e98-4b0d-5818-9efc-c9fd3010a527"), "ingredient", "en", "Rice" },
                    { new Guid("6bbf5e98-4b0d-5818-9efc-c9fd3010a527"), "ingredient", "nb", "Ris" },
                    { new Guid("6c2abe88-f414-54a9-8516-1bdc57f79b80"), "ingredient", "en", "Curry powder" },
                    { new Guid("6c2abe88-f414-54a9-8516-1bdc57f79b80"), "ingredient", "nb", "Karri" },
                    { new Guid("6e596366-2e86-5b36-ae83-f0d5749150a1"), "ingredient", "en", "Tomato" },
                    { new Guid("6e596366-2e86-5b36-ae83-f0d5749150a1"), "ingredient", "nb", "Tomat" },
                    { new Guid("6eb278e9-119d-5994-af30-45094f1072bd"), "ingredient", "en", "Cumin" },
                    { new Guid("6eb278e9-119d-5994-af30-45094f1072bd"), "ingredient", "nb", "Spisskummen" },
                    { new Guid("722e16a9-d3ff-5004-9772-10933d4fe6bd"), "ingredient", "en", "Thyme" },
                    { new Guid("722e16a9-d3ff-5004-9772-10933d4fe6bd"), "ingredient", "nb", "Timian" },
                    { new Guid("74a75ef6-eb74-5fe8-930a-6359f770bd74"), "ingredient", "en", "Kidney beans" },
                    { new Guid("74a75ef6-eb74-5fe8-930a-6359f770bd74"), "ingredient", "nb", "Kidneybønner" },
                    { new Guid("78b635b5-8e1f-505f-a6dc-de67f8593ec1"), "ingredient", "en", "Black pepper" },
                    { new Guid("78b635b5-8e1f-505f-a6dc-de67f8593ec1"), "ingredient", "nb", "Sort pepper" },
                    { new Guid("79178d43-875e-52fa-83a7-33583762c88c"), "ingredient", "en", "Ketchup" },
                    { new Guid("79178d43-875e-52fa-83a7-33583762c88c"), "ingredient", "nb", "Ketchup" },
                    { new Guid("792a6751-38e0-5406-a86d-cf376c44014d"), "ingredient", "en", "Pasta" },
                    { new Guid("792a6751-38e0-5406-a86d-cf376c44014d"), "ingredient", "nb", "Pasta" },
                    { new Guid("79b6b702-e4f9-5017-9a69-367c785fcaf8"), "ingredient", "en", "Oregano" },
                    { new Guid("79b6b702-e4f9-5017-9a69-367c785fcaf8"), "ingredient", "nb", "Oregano" },
                    { new Guid("7bb12c8d-a801-55bd-b957-f04f33baddf2"), "ingredient", "en", "Garlic" },
                    { new Guid("7bb12c8d-a801-55bd-b957-f04f33baddf2"), "ingredient", "nb", "Hvitløk" },
                    { new Guid("7c3ea56c-14ca-5a91-afdc-f568bf2a44f9"), "ingredient", "en", "Cod" },
                    { new Guid("7c3ea56c-14ca-5a91-afdc-f568bf2a44f9"), "ingredient", "nb", "Torsk" },
                    { new Guid("7c5a3a77-7111-56d6-82ac-12d5fa52c10c"), "ingredient", "en", "Broccoli" },
                    { new Guid("7c5a3a77-7111-56d6-82ac-12d5fa52c10c"), "ingredient", "nb", "Brokkoli" },
                    { new Guid("7d4f5a75-b5cb-5e1d-9db0-44d5f4e16256"), "ingredient", "en", "Syrup" },
                    { new Guid("7d4f5a75-b5cb-5e1d-9db0-44d5f4e16256"), "ingredient", "nb", "Sirup" },
                    { new Guid("80db0d1f-6512-58a7-a507-887a43683457"), "ingredient", "en", "Leek" },
                    { new Guid("80db0d1f-6512-58a7-a507-887a43683457"), "ingredient", "nb", "Purre" },
                    { new Guid("810748fa-acf5-538c-b92e-93de60765fb8"), "ingredient", "en", "Cheese" },
                    { new Guid("810748fa-acf5-538c-b92e-93de60765fb8"), "ingredient", "nb", "Ost" },
                    { new Guid("817f66e7-a5b1-57f8-9126-3047278e0740"), "ingredient", "en", "Lettuce" },
                    { new Guid("817f66e7-a5b1-57f8-9126-3047278e0740"), "ingredient", "nb", "Salat" },
                    { new Guid("81be2532-c29d-5f62-a1da-1985004d8435"), "ingredient", "en", "Butter" },
                    { new Guid("81be2532-c29d-5f62-a1da-1985004d8435"), "ingredient", "nb", "Smør" },
                    { new Guid("86d85834-4e8c-5e1a-b079-556dde755928"), "ingredient", "en", "Salmon" },
                    { new Guid("86d85834-4e8c-5e1a-b079-556dde755928"), "ingredient", "nb", "Laks" },
                    { new Guid("906be377-84d2-5135-b6d5-4d95e9242255"), "ingredient", "en", "Spinach" },
                    { new Guid("906be377-84d2-5135-b6d5-4d95e9242255"), "ingredient", "nb", "Spinat" },
                    { new Guid("91ea51af-b23d-525a-8f73-4b3a9b1579d7"), "ingredient", "en", "Mushrooms" },
                    { new Guid("91ea51af-b23d-525a-8f73-4b3a9b1579d7"), "ingredient", "nb", "Sopp" },
                    { new Guid("9239cd66-0bdd-5d2f-90be-fdd746ebdc98"), "ingredient", "en", "Chocolate" },
                    { new Guid("9239cd66-0bdd-5d2f-90be-fdd746ebdc98"), "ingredient", "nb", "Sjokolade" },
                    { new Guid("9455e9ad-ee5e-525c-81af-05f95ddaebcf"), "ingredient", "en", "Icing sugar" },
                    { new Guid("9455e9ad-ee5e-525c-81af-05f95ddaebcf"), "ingredient", "nb", "Melis" },
                    { new Guid("9889b811-51f0-5a78-911f-ddf64315f5dd"), "ingredient", "en", "Chicken thighs" },
                    { new Guid("9889b811-51f0-5a78-911f-ddf64315f5dd"), "ingredient", "nb", "Kyllinglår" },
                    { new Guid("991a40d6-f019-532c-98ab-cf03a2ff1ef0"), "ingredient", "en", "Brown cheese" },
                    { new Guid("991a40d6-f019-532c-98ab-cf03a2ff1ef0"), "ingredient", "nb", "Brunost" },
                    { new Guid("a17d723e-660b-5394-bacf-3f4a7f1dd119"), "ingredient", "en", "Parmesan" },
                    { new Guid("a17d723e-660b-5394-bacf-3f4a7f1dd119"), "ingredient", "nb", "Parmesan" },
                    { new Guid("a994e67c-5ac7-5793-b00f-546bcc2a1822"), "ingredient", "en", "Banana" },
                    { new Guid("a994e67c-5ac7-5793-b00f-546bcc2a1822"), "ingredient", "nb", "Banan" },
                    { new Guid("aab25390-c788-518b-aaef-0cb573a5ba10"), "ingredient", "en", "Cottage cheese" },
                    { new Guid("aab25390-c788-518b-aaef-0cb573a5ba10"), "ingredient", "nb", "Cottage cheese" },
                    { new Guid("ab733b3b-71d1-5d14-99a5-32905ee589ed"), "ingredient", "en", "Olive oil" },
                    { new Guid("ab733b3b-71d1-5d14-99a5-32905ee589ed"), "ingredient", "nb", "Olivenolje" },
                    { new Guid("ac11796b-2f44-52fb-8524-6fa2b3340a6c"), "ingredient", "en", "Cocoa" },
                    { new Guid("ac11796b-2f44-52fb-8524-6fa2b3340a6c"), "ingredient", "nb", "Kakao" },
                    { new Guid("ae6ce0de-ba86-5cb2-a56f-a0c8449a0ca6"), "ingredient", "en", "Crème fraîche" },
                    { new Guid("ae6ce0de-ba86-5cb2-a56f-a0c8449a0ca6"), "ingredient", "nb", "Crème fraîche" },
                    { new Guid("b1c9c75b-ca8c-5b1d-931b-7ae58472b2f4"), "ingredient", "en", "White cabbage" },
                    { new Guid("b1c9c75b-ca8c-5b1d-931b-7ae58472b2f4"), "ingredient", "nb", "Hvitkål" },
                    { new Guid("b3daf0da-3cb7-5aec-bf47-ed04f352bec0"), "ingredient", "en", "Rapeseed oil" },
                    { new Guid("b3daf0da-3cb7-5aec-bf47-ed04f352bec0"), "ingredient", "nb", "Rapsolje" },
                    { new Guid("b86a1379-8389-5a39-9081-14c0e331f2cd"), "ingredient", "en", "Yeast" },
                    { new Guid("b86a1379-8389-5a39-9081-14c0e331f2cd"), "ingredient", "nb", "Gjær" },
                    { new Guid("b9f18d0b-f60a-5c28-9838-37fe57d7e2cc"), "ingredient", "en", "Cream" },
                    { new Guid("b9f18d0b-f60a-5c28-9838-37fe57d7e2cc"), "ingredient", "nb", "Fløte" },
                    { new Guid("ba45e4fc-b4a3-55b3-b0b7-dcfb22358b0e"), "ingredient", "en", "Bread" },
                    { new Guid("ba45e4fc-b4a3-55b3-b0b7-dcfb22358b0e"), "ingredient", "nb", "Brød" },
                    { new Guid("bedbb7cc-d514-5978-a105-6671fa93e8c3"), "ingredient", "en", "Lentils" },
                    { new Guid("bedbb7cc-d514-5978-a105-6671fa93e8c3"), "ingredient", "nb", "Linser" },
                    { new Guid("c223976c-0aec-54e8-b637-0bffe6cd80c5"), "ingredient", "en", "Cinnamon" },
                    { new Guid("c223976c-0aec-54e8-b637-0bffe6cd80c5"), "ingredient", "nb", "Kanel" },
                    { new Guid("c253a6bb-412a-5308-8ba4-ff2ffba19662"), "ingredient", "en", "Bell pepper" },
                    { new Guid("c253a6bb-412a-5308-8ba4-ff2ffba19662"), "ingredient", "nb", "Paprika" },
                    { new Guid("c2b92c25-7566-522e-b817-beedfe24fd49"), "ingredient", "en", "Water" },
                    { new Guid("c2b92c25-7566-522e-b817-beedfe24fd49"), "ingredient", "nb", "Vann" },
                    { new Guid("c66300ab-d1cc-516a-b548-7428baa2cef0"), "ingredient", "en", "Ginger" },
                    { new Guid("c66300ab-d1cc-516a-b548-7428baa2cef0"), "ingredient", "nb", "Ingefær" },
                    { new Guid("cc94a551-2fb1-5df4-ba10-27663092ade6"), "ingredient", "en", "Cooking cream" },
                    { new Guid("cc94a551-2fb1-5df4-ba10-27663092ade6"), "ingredient", "nb", "Matfløte" },
                    { new Guid("cef2dcdd-aab6-56b0-b4a7-cf69803006e2"), "ingredient", "en", "Baking powder" },
                    { new Guid("cef2dcdd-aab6-56b0-b4a7-cf69803006e2"), "ingredient", "nb", "Bakepulver" },
                    { new Guid("d25c1950-1087-560e-998d-498e10cf418e"), "ingredient", "en", "Sugar" },
                    { new Guid("d25c1950-1087-560e-998d-498e10cf418e"), "ingredient", "nb", "Sukker" },
                    { new Guid("d4e4de1c-5557-5d29-b2c7-ea8f29035f8a"), "ingredient", "en", "Corn" },
                    { new Guid("d4e4de1c-5557-5d29-b2c7-ea8f29035f8a"), "ingredient", "nb", "Mais" },
                    { new Guid("d8fa323d-76da-59c0-a895-1924aa8a3ca0"), "ingredient", "en", "Mayonnaise" },
                    { new Guid("d8fa323d-76da-59c0-a895-1924aa8a3ca0"), "ingredient", "nb", "Majones" },
                    { new Guid("da5739f3-0171-54c0-834f-f0ea0632e189"), "ingredient", "en", "Baking soda" },
                    { new Guid("da5739f3-0171-54c0-834f-f0ea0632e189"), "ingredient", "nb", "Natron" },
                    { new Guid("da9bf5f7-9cdb-563d-91aa-38553c382aa6"), "ingredient", "en", "Chives" },
                    { new Guid("da9bf5f7-9cdb-563d-91aa-38553c382aa6"), "ingredient", "nb", "Gressløk" },
                    { new Guid("dd4248ca-3750-5bf7-b7e7-610a6388f7cf"), "ingredient", "en", "Honey" },
                    { new Guid("dd4248ca-3750-5bf7-b7e7-610a6388f7cf"), "ingredient", "nb", "Honning" },
                    { new Guid("dff32bc5-07cd-59dd-9ba1-46f7e110ff57"), "ingredient", "en", "Onion" },
                    { new Guid("dff32bc5-07cd-59dd-9ba1-46f7e110ff57"), "ingredient", "nb", "Løk" },
                    { new Guid("e0ba0668-a3ec-50ba-9063-115da67c2e6a"), "ingredient", "en", "Salt" },
                    { new Guid("e0ba0668-a3ec-50ba-9063-115da67c2e6a"), "ingredient", "nb", "Salt" },
                    { new Guid("e2221128-f4f3-5415-9ad2-2a3444660f18"), "ingredient", "en", "Sweet potato" },
                    { new Guid("e2221128-f4f3-5415-9ad2-2a3444660f18"), "ingredient", "nb", "Søtpotet" },
                    { new Guid("e387d7f6-3531-5fac-8ff5-1828c0a487f0"), "ingredient", "en", "Nutmeg" },
                    { new Guid("e387d7f6-3531-5fac-8ff5-1828c0a487f0"), "ingredient", "nb", "Muskat" },
                    { new Guid("e995deeb-fa91-5698-ab94-caf43f7c4cc8"), "ingredient", "en", "Cucumber" },
                    { new Guid("e995deeb-fa91-5698-ab94-caf43f7c4cc8"), "ingredient", "nb", "Agurk" },
                    { new Guid("ece38aeb-5105-5eef-b921-af6c0f1c3ce0"), "ingredient", "en", "Tuna" },
                    { new Guid("ece38aeb-5105-5eef-b921-af6c0f1c3ce0"), "ingredient", "nb", "Tunfisk" },
                    { new Guid("ed472bba-a9e2-5b5c-89f9-296472b8617b"), "ingredient", "en", "Paprika powder" },
                    { new Guid("ed472bba-a9e2-5b5c-89f9-296472b8617b"), "ingredient", "nb", "Paprikapulver" },
                    { new Guid("f820ab7f-4d7f-51ec-a99b-53196dbdc901"), "ingredient", "en", "Chickpeas" },
                    { new Guid("f820ab7f-4d7f-51ec-a99b-53196dbdc901"), "ingredient", "nb", "Kikerter" },
                    { new Guid("f8986fd1-a524-5ff0-80e3-9795f0d5de2d"), "ingredient", "en", "Milk" },
                    { new Guid("f8986fd1-a524-5ff0-80e3-9795f0d5de2d"), "ingredient", "nb", "Melk" },
                    { new Guid("f89cfb7c-b140-5315-be9b-31c60bb5f945"), "ingredient", "en", "Zucchini" },
                    { new Guid("f89cfb7c-b140-5315-be9b-31c60bb5f945"), "ingredient", "nb", "Squash" },
                    { new Guid("fa28a23a-4e65-50c5-b127-f3e6b1bd37d9"), "ingredient", "en", "Lime" },
                    { new Guid("fa28a23a-4e65-50c5-b127-f3e6b1bd37d9"), "ingredient", "nb", "Lime" },
                    { new Guid("fe6a6905-3590-54db-8bc7-5f9be03dffde"), "ingredient", "en", "Vanilla sugar" },
                    { new Guid("fe6a6905-3590-54db-8bc7-5f9be03dffde"), "ingredient", "nb", "Vaniljesukker" },
                    { new Guid("116bd925-5e28-5aa6-b8d7-1be4b654428b"), "tag", "en", "Dinner" },
                    { new Guid("116bd925-5e28-5aa6-b8d7-1be4b654428b"), "tag", "nb", "Middag" },
                    { new Guid("1b7b0e72-5763-5bbf-abbd-dd710f275db5"), "tag", "en", "Vegetarian" },
                    { new Guid("1b7b0e72-5763-5bbf-abbd-dd710f275db5"), "tag", "nb", "Vegetar" },
                    { new Guid("275e7f12-8016-5cca-86bc-24e60a545b02"), "tag", "en", "Meat" },
                    { new Guid("275e7f12-8016-5cca-86bc-24e60a545b02"), "tag", "nb", "Kjøtt" },
                    { new Guid("3499a5e6-0a40-5a7a-913a-e4a3bf9a0d78"), "tag", "en", "Breakfast" },
                    { new Guid("3499a5e6-0a40-5a7a-913a-e4a3bf9a0d78"), "tag", "nb", "Frokost" },
                    { new Guid("8926186c-b8c9-577f-a055-26b2036e118c"), "tag", "en", "Kid friendly" },
                    { new Guid("8926186c-b8c9-577f-a055-26b2036e118c"), "tag", "nb", "Barnevennlig" },
                    { new Guid("9783fe91-642e-54d7-94ed-6c6daf55afc4"), "tag", "en", "Baking" },
                    { new Guid("9783fe91-642e-54d7-94ed-6c6daf55afc4"), "tag", "nb", "Bakst" },
                    { new Guid("a6f985f6-8c31-526b-9090-1115c56cde39"), "tag", "en", "Fish" },
                    { new Guid("a6f985f6-8c31-526b-9090-1115c56cde39"), "tag", "nb", "Fisk" },
                    { new Guid("b1b9d3d6-f8bf-5c24-a830-0da73ab73b11"), "tag", "en", "Soup" },
                    { new Guid("b1b9d3d6-f8bf-5c24-a830-0da73ab73b11"), "tag", "nb", "Suppe" },
                    { new Guid("c9540730-73c0-5f9e-af48-8dbebeaf9f04"), "tag", "en", "Chicken" },
                    { new Guid("c9540730-73c0-5f9e-af48-8dbebeaf9f04"), "tag", "nb", "Kylling" },
                    { new Guid("e15c99ac-dd7b-516a-9e39-ea1881b8798b"), "tag", "en", "Lunch" },
                    { new Guid("e15c99ac-dd7b-516a-9e39-ea1881b8798b"), "tag", "nb", "Lunsj" },
                    { new Guid("ea055abc-60e0-5d30-8147-3b23d030c8a2"), "tag", "en", "Pasta" },
                    { new Guid("ea055abc-60e0-5d30-8147-3b23d030c8a2"), "tag", "nb", "Pasta" },
                    { new Guid("fadf24a6-74f5-5991-a159-54e29d3489f1"), "tag", "en", "Dessert" },
                    { new Guid("fadf24a6-74f5-5991-a159-54e29d3489f1"), "tag", "nb", "Dessert" },
                    { new Guid("fedc36ae-081b-5129-81d0-3e9ee1f7746c"), "tag", "en", "Quick" },
                    { new Guid("fedc36ae-081b-5129-81d0-3e9ee1f7746c"), "tag", "nb", "Rask" },
                    { new Guid("0b27c324-159b-513c-8bcb-a11f7da2247f"), "unit", "en", "cup" },
                    { new Guid("0b27c324-159b-513c-8bcb-a11f7da2247f"), "unit", "nb", "kopp" },
                    { new Guid("21341ca2-75fd-5c45-864a-084b06676bc3"), "unit", "en", "gram" },
                    { new Guid("21341ca2-75fd-5c45-864a-084b06676bc3"), "unit", "nb", "gram" },
                    { new Guid("5bd4a484-e162-589c-a8db-456552bb256f"), "unit", "en", "pinch" },
                    { new Guid("5bd4a484-e162-589c-a8db-456552bb256f"), "unit", "nb", "klype" },
                    { new Guid("668e1f20-d880-5cf9-8120-e4d65e55bb52"), "unit", "en", "kilogram" },
                    { new Guid("668e1f20-d880-5cf9-8120-e4d65e55bb52"), "unit", "nb", "kilogram" },
                    { new Guid("745cb6dc-58a7-5454-baa5-d2b9e5faf17e"), "unit", "en", "tablespoon" },
                    { new Guid("745cb6dc-58a7-5454-baa5-d2b9e5faf17e"), "unit", "nb", "spiseskje" },
                    { new Guid("74ab4d15-0762-5bb2-88b9-a0a57179b878"), "unit", "en", "deciliter" },
                    { new Guid("74ab4d15-0762-5bb2-88b9-a0a57179b878"), "unit", "nb", "desiliter" },
                    { new Guid("780932a4-30a7-5208-8edc-b185679fc00c"), "unit", "en", "teaspoon" },
                    { new Guid("780932a4-30a7-5208-8edc-b185679fc00c"), "unit", "nb", "teskje" },
                    { new Guid("bd160927-abba-5cdb-883e-f18ee85434c2"), "unit", "en", "liter" },
                    { new Guid("bd160927-abba-5cdb-883e-f18ee85434c2"), "unit", "nb", "liter" },
                    { new Guid("bd92778d-4bcc-5c00-a80d-1bd1a08afb58"), "unit", "en", "piece" },
                    { new Guid("bd92778d-4bcc-5c00-a80d-1bd1a08afb58"), "unit", "nb", "stykk" },
                    { new Guid("be6d83b2-31bd-5979-b8be-193939902ee1"), "unit", "en", "milliliter" },
                    { new Guid("be6d83b2-31bd-5979-b8be-193939902ee1"), "unit", "nb", "milliliter" },
                    { new Guid("c7969cfb-2cf1-5d5f-9855-504b47ed4faf"), "unit", "en", "clove" },
                    { new Guid("c7969cfb-2cf1-5d5f-9855-504b47ed4faf"), "unit", "nb", "fedd" }
                });

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("00b577aa-2127-533c-8a68-f9099001f88a"),
                column: "Name",
                value: "Cardamom");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("018b5367-aabf-5218-9a80-387c1eefa30a"),
                column: "Name",
                value: "Coconut milk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("05a6160e-87cd-5d2c-81df-c0a753ba5e19"),
                column: "Name",
                value: "Sour cream");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("062621ee-301b-53d1-aa7b-0e00f1c33081"),
                column: "Name",
                value: "Celery");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("079d2db4-8e75-543b-84fe-5c82a7963559"),
                column: "Name",
                value: "Ham");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0942b134-c6e7-5b24-b9c7-0aa52f8783d6"),
                column: "Name",
                value: "Spring onion");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0c6bbe3a-b113-52a5-9fbe-c680dc6fa614"),
                column: "Name",
                value: "Sausages");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0d7adfbb-5b54-5ae4-bd7e-ab5ea9de6b86"),
                column: "Name",
                value: "Apple");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0eb2fb2b-c95a-5044-99a3-d9453c52cc2d"),
                column: "Name",
                value: "Cornstarch");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0ede68df-ce20-58e2-a9fa-6cbeadf05b8b"),
                column: "Name",
                value: "Tortilla wraps");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("1105b71c-7f8b-537d-a00c-11f2d21798a2"),
                column: "Name",
                value: "Brown sugar");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("1127b196-6678-52fb-afe8-80ffd554e70c"),
                column: "Name",
                value: "Chicken breast fillet");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("12aaac43-57fb-55c3-979c-3a7cd2c71f22"),
                column: "Name",
                value: "Eggplant");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("12e6be7c-2862-509e-b602-b5bac890ca06"),
                column: "Name",
                value: "Wheat flour");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("14edc321-5475-52f2-9b9c-a8e58631d691"),
                column: "Name",
                value: "Peas");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("16bb8de3-129a-5b67-867e-969a2270c5e5"),
                column: "Name",
                value: "Strawberries");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("182bfd36-b47f-51af-b97d-c9c5e870636a"),
                column: "Name",
                value: "Canned tomatoes");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("18aeb9c6-f737-5ae7-a050-a69952a33d65"),
                column: "Name",
                value: "Rolled oats");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("1e775c0f-11de-56e3-9e48-162a8ce306b5"),
                column: "Name",
                value: "Bay leaf");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("22fdd055-6217-5f93-93a4-e2c4fad857cb"),
                column: "Name",
                value: "Soy sauce");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2437ea85-570e-5944-8af8-0a9572fae257"),
                column: "Name",
                value: "Basil");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("25530a97-c780-5914-b22c-c281940b37e1"),
                column: "Name",
                value: "Tomato puree");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("28f29641-7731-5158-ad85-90ace26007a6"),
                column: "Name",
                value: "Cauliflower");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2c102b4b-c557-59f1-ba38-fbb7edef5a2b"),
                column: "Name",
                value: "Fish fingers");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2f80230b-4d31-56e2-b12f-f79776e21680"),
                column: "Name",
                value: "Pork chop");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("302edd54-59dc-5145-b469-c988cf1f3694"),
                column: "Name",
                value: "Red onion");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("340165f7-81cb-5452-8e49-a5a4e02d3962"),
                column: "Name",
                value: "Minced meat");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("358bbaa7-68e5-5ebb-8bd7-adaa02707a1f"),
                column: "Name",
                value: "Peanut butter");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("481a68a8-b863-58be-87c5-0f2860de7f0e"),
                column: "Name",
                value: "Lemon");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("48f5afed-5b6d-5e25-af57-48a47694d3f7"),
                column: "Name",
                value: "Mustard");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("4cc2ada6-73b1-506f-818f-c5855d935d73"),
                column: "Name",
                value: "Parsley");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("4e968abd-1dfe-579d-acf9-de5fdfeae671"),
                column: "Name",
                value: "Vinegar");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("578d165a-9764-56e3-8eee-7979def50aef"),
                column: "Name",
                value: "Carrot");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("5a1af167-140c-561c-a977-8aefd9618c68"),
                column: "Name",
                value: "Noodles");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("5a4a9fbe-71e7-55a1-bd65-e5a626e66c51"),
                column: "Name",
                value: "Orange");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("60cfdd20-06dd-509e-91d6-df0929a098c7"),
                column: "Name",
                value: "Blueberries");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("62629f8f-0cb8-5245-a091-6a7fe95b914b"),
                column: "Name",
                value: "Stock cube");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6871d5c3-f983-5046-870b-095b26f4c9b3"),
                column: "Name",
                value: "Plain yoghurt");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6968fff3-8d67-5bca-ac32-0caea672f02b"),
                column: "Name",
                value: "Shrimp");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6aca22ad-6eff-588f-b605-fb3d2c81df89"),
                column: "Name",
                value: "Potato");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6bbf5e98-4b0d-5818-9efc-c9fd3010a527"),
                column: "Name",
                value: "Rice");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6c2abe88-f414-54a9-8516-1bdc57f79b80"),
                column: "Name",
                value: "Curry powder");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6e596366-2e86-5b36-ae83-f0d5749150a1"),
                column: "Name",
                value: "Tomato");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6eb278e9-119d-5994-af30-45094f1072bd"),
                column: "Name",
                value: "Cumin");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("722e16a9-d3ff-5004-9772-10933d4fe6bd"),
                column: "Name",
                value: "Thyme");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("74a75ef6-eb74-5fe8-930a-6359f770bd74"),
                column: "Name",
                value: "Kidney beans");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("78b635b5-8e1f-505f-a6dc-de67f8593ec1"),
                column: "Name",
                value: "Black pepper");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7bb12c8d-a801-55bd-b957-f04f33baddf2"),
                column: "Name",
                value: "Garlic");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7c3ea56c-14ca-5a91-afdc-f568bf2a44f9"),
                column: "Name",
                value: "Cod");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7c5a3a77-7111-56d6-82ac-12d5fa52c10c"),
                column: "Name",
                value: "Broccoli");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7d4f5a75-b5cb-5e1d-9db0-44d5f4e16256"),
                column: "Name",
                value: "Syrup");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("80db0d1f-6512-58a7-a507-887a43683457"),
                column: "Name",
                value: "Leek");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("810748fa-acf5-538c-b92e-93de60765fb8"),
                column: "Name",
                value: "Cheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("817f66e7-a5b1-57f8-9126-3047278e0740"),
                column: "Name",
                value: "Lettuce");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("81be2532-c29d-5f62-a1da-1985004d8435"),
                column: "Name",
                value: "Butter");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("86d85834-4e8c-5e1a-b079-556dde755928"),
                column: "Name",
                value: "Salmon");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("906be377-84d2-5135-b6d5-4d95e9242255"),
                column: "Name",
                value: "Spinach");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("91ea51af-b23d-525a-8f73-4b3a9b1579d7"),
                column: "Name",
                value: "Mushrooms");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("9239cd66-0bdd-5d2f-90be-fdd746ebdc98"),
                column: "Name",
                value: "Chocolate");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("9455e9ad-ee5e-525c-81af-05f95ddaebcf"),
                column: "Name",
                value: "Icing sugar");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("9889b811-51f0-5a78-911f-ddf64315f5dd"),
                column: "Name",
                value: "Chicken thighs");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("991a40d6-f019-532c-98ab-cf03a2ff1ef0"),
                column: "Name",
                value: "Brown cheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("a994e67c-5ac7-5793-b00f-546bcc2a1822"),
                column: "Name",
                value: "Banana");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ab733b3b-71d1-5d14-99a5-32905ee589ed"),
                column: "Name",
                value: "Olive oil");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ac11796b-2f44-52fb-8524-6fa2b3340a6c"),
                column: "Name",
                value: "Cocoa");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b1c9c75b-ca8c-5b1d-931b-7ae58472b2f4"),
                column: "Name",
                value: "White cabbage");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b3daf0da-3cb7-5aec-bf47-ed04f352bec0"),
                column: "Name",
                value: "Rapeseed oil");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b86a1379-8389-5a39-9081-14c0e331f2cd"),
                column: "Name",
                value: "Yeast");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b9f18d0b-f60a-5c28-9838-37fe57d7e2cc"),
                column: "Name",
                value: "Cream");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ba45e4fc-b4a3-55b3-b0b7-dcfb22358b0e"),
                column: "Name",
                value: "Bread");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("bedbb7cc-d514-5978-a105-6671fa93e8c3"),
                column: "Name",
                value: "Lentils");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c223976c-0aec-54e8-b637-0bffe6cd80c5"),
                column: "Name",
                value: "Cinnamon");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c253a6bb-412a-5308-8ba4-ff2ffba19662"),
                column: "Name",
                value: "Bell pepper");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c2b92c25-7566-522e-b817-beedfe24fd49"),
                column: "Name",
                value: "Water");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c66300ab-d1cc-516a-b548-7428baa2cef0"),
                column: "Name",
                value: "Ginger");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("cc94a551-2fb1-5df4-ba10-27663092ade6"),
                column: "Name",
                value: "Cooking cream");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("cef2dcdd-aab6-56b0-b4a7-cf69803006e2"),
                column: "Name",
                value: "Baking powder");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d25c1950-1087-560e-998d-498e10cf418e"),
                column: "Name",
                value: "Sugar");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d4e4de1c-5557-5d29-b2c7-ea8f29035f8a"),
                column: "Name",
                value: "Corn");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d8fa323d-76da-59c0-a895-1924aa8a3ca0"),
                column: "Name",
                value: "Mayonnaise");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("da5739f3-0171-54c0-834f-f0ea0632e189"),
                column: "Name",
                value: "Baking soda");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("da9bf5f7-9cdb-563d-91aa-38553c382aa6"),
                column: "Name",
                value: "Chives");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("dd4248ca-3750-5bf7-b7e7-610a6388f7cf"),
                column: "Name",
                value: "Honey");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("dff32bc5-07cd-59dd-9ba1-46f7e110ff57"),
                column: "Name",
                value: "Onion");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e2221128-f4f3-5415-9ad2-2a3444660f18"),
                column: "Name",
                value: "Sweet potato");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e387d7f6-3531-5fac-8ff5-1828c0a487f0"),
                column: "Name",
                value: "Nutmeg");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e995deeb-fa91-5698-ab94-caf43f7c4cc8"),
                column: "Name",
                value: "Cucumber");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ece38aeb-5105-5eef-b921-af6c0f1c3ce0"),
                column: "Name",
                value: "Tuna");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ed472bba-a9e2-5b5c-89f9-296472b8617b"),
                column: "Name",
                value: "Paprika powder");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("f820ab7f-4d7f-51ec-a99b-53196dbdc901"),
                column: "Name",
                value: "Chickpeas");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("f8986fd1-a524-5ff0-80e3-9795f0d5de2d"),
                column: "Name",
                value: "Milk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("f89cfb7c-b140-5315-be9b-31c60bb5f945"),
                column: "Name",
                value: "Zucchini");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("fe6a6905-3590-54db-8bc7-5f9be03dffde"),
                column: "Name",
                value: "Vanilla sugar");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("116bd925-5e28-5aa6-b8d7-1be4b654428b"),
                column: "Name",
                value: "Dinner");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("1b7b0e72-5763-5bbf-abbd-dd710f275db5"),
                column: "Name",
                value: "Vegetarian");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("275e7f12-8016-5cca-86bc-24e60a545b02"),
                column: "Name",
                value: "Meat");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("3499a5e6-0a40-5a7a-913a-e4a3bf9a0d78"),
                column: "Name",
                value: "Breakfast");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("8926186c-b8c9-577f-a055-26b2036e118c"),
                column: "Name",
                value: "Kid friendly");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("9783fe91-642e-54d7-94ed-6c6daf55afc4"),
                column: "Name",
                value: "Baking");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("a6f985f6-8c31-526b-9090-1115c56cde39"),
                column: "Name",
                value: "Fish");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("b1b9d3d6-f8bf-5c24-a830-0da73ab73b11"),
                column: "Name",
                value: "Soup");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("c9540730-73c0-5f9e-af48-8dbebeaf9f04"),
                column: "Name",
                value: "Chicken");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("e15c99ac-dd7b-516a-9e39-ea1881b8798b"),
                column: "Name",
                value: "Lunch");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("fedc36ae-081b-5129-81d0-3e9ee1f7746c"),
                column: "Name",
                value: "Quick");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("0b27c324-159b-513c-8bcb-a11f7da2247f"),
                column: "Name",
                value: "cup");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("5bd4a484-e162-589c-a8db-456552bb256f"),
                column: "Name",
                value: "pinch");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("745cb6dc-58a7-5454-baa5-d2b9e5faf17e"),
                column: "Name",
                value: "tablespoon");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("74ab4d15-0762-5bb2-88b9-a0a57179b878"),
                column: "Name",
                value: "deciliter");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("780932a4-30a7-5208-8edc-b185679fc00c"),
                column: "Name",
                value: "teaspoon");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("bd92778d-4bcc-5c00-a80d-1bd1a08afb58"),
                column: "Name",
                value: "piece");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("c7969cfb-2cf1-5d5f-9855-504b47ed4faf"),
                column: "Name",
                value: "clove");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTranslations_Kind_Name",
                table: "CatalogTranslations",
                columns: new[] { "Kind", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogTranslations");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("00b577aa-2127-533c-8a68-f9099001f88a"),
                column: "Name",
                value: "Kardemomme");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("018b5367-aabf-5218-9a80-387c1eefa30a"),
                column: "Name",
                value: "Kokosmelk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("05a6160e-87cd-5d2c-81df-c0a753ba5e19"),
                column: "Name",
                value: "Rømme");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("062621ee-301b-53d1-aa7b-0e00f1c33081"),
                column: "Name",
                value: "Selleri");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("079d2db4-8e75-543b-84fe-5c82a7963559"),
                column: "Name",
                value: "Skinke");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0942b134-c6e7-5b24-b9c7-0aa52f8783d6"),
                column: "Name",
                value: "Vårløk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0c6bbe3a-b113-52a5-9fbe-c680dc6fa614"),
                column: "Name",
                value: "Pølser");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0d7adfbb-5b54-5ae4-bd7e-ab5ea9de6b86"),
                column: "Name",
                value: "Eple");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0eb2fb2b-c95a-5044-99a3-d9453c52cc2d"),
                column: "Name",
                value: "Maisenna");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0ede68df-ce20-58e2-a9fa-6cbeadf05b8b"),
                column: "Name",
                value: "Tortillalefser");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("1105b71c-7f8b-537d-a00c-11f2d21798a2"),
                column: "Name",
                value: "Brunt sukker");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("1127b196-6678-52fb-afe8-80ffd554e70c"),
                column: "Name",
                value: "Kyllingfilet");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("12aaac43-57fb-55c3-979c-3a7cd2c71f22"),
                column: "Name",
                value: "Aubergine");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("12e6be7c-2862-509e-b602-b5bac890ca06"),
                column: "Name",
                value: "Hvetemel");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("14edc321-5475-52f2-9b9c-a8e58631d691"),
                column: "Name",
                value: "Erter");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("16bb8de3-129a-5b67-867e-969a2270c5e5"),
                column: "Name",
                value: "Jordbær");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("182bfd36-b47f-51af-b97d-c9c5e870636a"),
                column: "Name",
                value: "Hermetiske tomater");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("18aeb9c6-f737-5ae7-a050-a69952a33d65"),
                column: "Name",
                value: "Havregryn");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("1e775c0f-11de-56e3-9e48-162a8ce306b5"),
                column: "Name",
                value: "Laurbærblad");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("22fdd055-6217-5f93-93a4-e2c4fad857cb"),
                column: "Name",
                value: "Soyasaus");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2437ea85-570e-5944-8af8-0a9572fae257"),
                column: "Name",
                value: "Basilikum");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("25530a97-c780-5914-b22c-c281940b37e1"),
                column: "Name",
                value: "Tomatpuré");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("28f29641-7731-5158-ad85-90ace26007a6"),
                column: "Name",
                value: "Blomkål");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2c102b4b-c557-59f1-ba38-fbb7edef5a2b"),
                column: "Name",
                value: "Fiskepinner");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2f80230b-4d31-56e2-b12f-f79776e21680"),
                column: "Name",
                value: "Svinekotelett");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("302edd54-59dc-5145-b469-c988cf1f3694"),
                column: "Name",
                value: "Rødløk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("340165f7-81cb-5452-8e49-a5a4e02d3962"),
                column: "Name",
                value: "Kjøttdeig");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("358bbaa7-68e5-5ebb-8bd7-adaa02707a1f"),
                column: "Name",
                value: "Peanøttsmør");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("481a68a8-b863-58be-87c5-0f2860de7f0e"),
                column: "Name",
                value: "Sitron");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("48f5afed-5b6d-5e25-af57-48a47694d3f7"),
                column: "Name",
                value: "Sennep");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("4cc2ada6-73b1-506f-818f-c5855d935d73"),
                column: "Name",
                value: "Persille");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("4e968abd-1dfe-579d-acf9-de5fdfeae671"),
                column: "Name",
                value: "Eddik");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("578d165a-9764-56e3-8eee-7979def50aef"),
                column: "Name",
                value: "Gulrot");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("5a1af167-140c-561c-a977-8aefd9618c68"),
                column: "Name",
                value: "Nudler");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("5a4a9fbe-71e7-55a1-bd65-e5a626e66c51"),
                column: "Name",
                value: "Appelsin");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("60cfdd20-06dd-509e-91d6-df0929a098c7"),
                column: "Name",
                value: "Blåbær");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("62629f8f-0cb8-5245-a091-6a7fe95b914b"),
                column: "Name",
                value: "Buljongterning");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6871d5c3-f983-5046-870b-095b26f4c9b3"),
                column: "Name",
                value: "Yoghurt naturell");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6968fff3-8d67-5bca-ac32-0caea672f02b"),
                column: "Name",
                value: "Reker");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6aca22ad-6eff-588f-b605-fb3d2c81df89"),
                column: "Name",
                value: "Potet");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6bbf5e98-4b0d-5818-9efc-c9fd3010a527"),
                column: "Name",
                value: "Ris");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6c2abe88-f414-54a9-8516-1bdc57f79b80"),
                column: "Name",
                value: "Karri");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6e596366-2e86-5b36-ae83-f0d5749150a1"),
                column: "Name",
                value: "Tomat");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6eb278e9-119d-5994-af30-45094f1072bd"),
                column: "Name",
                value: "Spisskummen");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("722e16a9-d3ff-5004-9772-10933d4fe6bd"),
                column: "Name",
                value: "Timian");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("74a75ef6-eb74-5fe8-930a-6359f770bd74"),
                column: "Name",
                value: "Kidneybønner");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("78b635b5-8e1f-505f-a6dc-de67f8593ec1"),
                column: "Name",
                value: "Sort pepper");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7bb12c8d-a801-55bd-b957-f04f33baddf2"),
                column: "Name",
                value: "Hvitløk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7c3ea56c-14ca-5a91-afdc-f568bf2a44f9"),
                column: "Name",
                value: "Torsk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7c5a3a77-7111-56d6-82ac-12d5fa52c10c"),
                column: "Name",
                value: "Brokkoli");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7d4f5a75-b5cb-5e1d-9db0-44d5f4e16256"),
                column: "Name",
                value: "Sirup");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("80db0d1f-6512-58a7-a507-887a43683457"),
                column: "Name",
                value: "Purre");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("810748fa-acf5-538c-b92e-93de60765fb8"),
                column: "Name",
                value: "Ost");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("817f66e7-a5b1-57f8-9126-3047278e0740"),
                column: "Name",
                value: "Salat");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("81be2532-c29d-5f62-a1da-1985004d8435"),
                column: "Name",
                value: "Smør");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("86d85834-4e8c-5e1a-b079-556dde755928"),
                column: "Name",
                value: "Laks");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("906be377-84d2-5135-b6d5-4d95e9242255"),
                column: "Name",
                value: "Spinat");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("91ea51af-b23d-525a-8f73-4b3a9b1579d7"),
                column: "Name",
                value: "Sopp");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("9239cd66-0bdd-5d2f-90be-fdd746ebdc98"),
                column: "Name",
                value: "Sjokolade");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("9455e9ad-ee5e-525c-81af-05f95ddaebcf"),
                column: "Name",
                value: "Melis");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("9889b811-51f0-5a78-911f-ddf64315f5dd"),
                column: "Name",
                value: "Kyllinglår");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("991a40d6-f019-532c-98ab-cf03a2ff1ef0"),
                column: "Name",
                value: "Brunost");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("a994e67c-5ac7-5793-b00f-546bcc2a1822"),
                column: "Name",
                value: "Banan");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ab733b3b-71d1-5d14-99a5-32905ee589ed"),
                column: "Name",
                value: "Olivenolje");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ac11796b-2f44-52fb-8524-6fa2b3340a6c"),
                column: "Name",
                value: "Kakao");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b1c9c75b-ca8c-5b1d-931b-7ae58472b2f4"),
                column: "Name",
                value: "Hvitkål");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b3daf0da-3cb7-5aec-bf47-ed04f352bec0"),
                column: "Name",
                value: "Rapsolje");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b86a1379-8389-5a39-9081-14c0e331f2cd"),
                column: "Name",
                value: "Gjær");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b9f18d0b-f60a-5c28-9838-37fe57d7e2cc"),
                column: "Name",
                value: "Fløte");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ba45e4fc-b4a3-55b3-b0b7-dcfb22358b0e"),
                column: "Name",
                value: "Brød");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("bedbb7cc-d514-5978-a105-6671fa93e8c3"),
                column: "Name",
                value: "Linser");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c223976c-0aec-54e8-b637-0bffe6cd80c5"),
                column: "Name",
                value: "Kanel");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c253a6bb-412a-5308-8ba4-ff2ffba19662"),
                column: "Name",
                value: "Paprika");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c2b92c25-7566-522e-b817-beedfe24fd49"),
                column: "Name",
                value: "Vann");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c66300ab-d1cc-516a-b548-7428baa2cef0"),
                column: "Name",
                value: "Ingefær");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("cc94a551-2fb1-5df4-ba10-27663092ade6"),
                column: "Name",
                value: "Matfløte");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("cef2dcdd-aab6-56b0-b4a7-cf69803006e2"),
                column: "Name",
                value: "Bakepulver");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d25c1950-1087-560e-998d-498e10cf418e"),
                column: "Name",
                value: "Sukker");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d4e4de1c-5557-5d29-b2c7-ea8f29035f8a"),
                column: "Name",
                value: "Mais");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d8fa323d-76da-59c0-a895-1924aa8a3ca0"),
                column: "Name",
                value: "Majones");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("da5739f3-0171-54c0-834f-f0ea0632e189"),
                column: "Name",
                value: "Natron");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("da9bf5f7-9cdb-563d-91aa-38553c382aa6"),
                column: "Name",
                value: "Gressløk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("dd4248ca-3750-5bf7-b7e7-610a6388f7cf"),
                column: "Name",
                value: "Honning");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("dff32bc5-07cd-59dd-9ba1-46f7e110ff57"),
                column: "Name",
                value: "Løk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e2221128-f4f3-5415-9ad2-2a3444660f18"),
                column: "Name",
                value: "Søtpotet");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e387d7f6-3531-5fac-8ff5-1828c0a487f0"),
                column: "Name",
                value: "Muskat");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e995deeb-fa91-5698-ab94-caf43f7c4cc8"),
                column: "Name",
                value: "Agurk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ece38aeb-5105-5eef-b921-af6c0f1c3ce0"),
                column: "Name",
                value: "Tunfisk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ed472bba-a9e2-5b5c-89f9-296472b8617b"),
                column: "Name",
                value: "Paprikapulver");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("f820ab7f-4d7f-51ec-a99b-53196dbdc901"),
                column: "Name",
                value: "Kikerter");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("f8986fd1-a524-5ff0-80e3-9795f0d5de2d"),
                column: "Name",
                value: "Melk");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("f89cfb7c-b140-5315-be9b-31c60bb5f945"),
                column: "Name",
                value: "Squash");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("fe6a6905-3590-54db-8bc7-5f9be03dffde"),
                column: "Name",
                value: "Vaniljesukker");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("116bd925-5e28-5aa6-b8d7-1be4b654428b"),
                column: "Name",
                value: "Middag");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("1b7b0e72-5763-5bbf-abbd-dd710f275db5"),
                column: "Name",
                value: "Vegetar");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("275e7f12-8016-5cca-86bc-24e60a545b02"),
                column: "Name",
                value: "Kjøtt");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("3499a5e6-0a40-5a7a-913a-e4a3bf9a0d78"),
                column: "Name",
                value: "Frokost");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("8926186c-b8c9-577f-a055-26b2036e118c"),
                column: "Name",
                value: "Barnevennlig");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("9783fe91-642e-54d7-94ed-6c6daf55afc4"),
                column: "Name",
                value: "Bakst");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("a6f985f6-8c31-526b-9090-1115c56cde39"),
                column: "Name",
                value: "Fisk");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("b1b9d3d6-f8bf-5c24-a830-0da73ab73b11"),
                column: "Name",
                value: "Suppe");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("c9540730-73c0-5f9e-af48-8dbebeaf9f04"),
                column: "Name",
                value: "Kylling");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("e15c99ac-dd7b-516a-9e39-ea1881b8798b"),
                column: "Name",
                value: "Lunsj");

            migrationBuilder.UpdateData(
                table: "Tags",
                keyColumn: "Id",
                keyValue: new Guid("fedc36ae-081b-5129-81d0-3e9ee1f7746c"),
                column: "Name",
                value: "Rask");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("0b27c324-159b-513c-8bcb-a11f7da2247f"),
                column: "Name",
                value: "kopp");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("5bd4a484-e162-589c-a8db-456552bb256f"),
                column: "Name",
                value: "klype");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("745cb6dc-58a7-5454-baa5-d2b9e5faf17e"),
                column: "Name",
                value: "spiseskje");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("74ab4d15-0762-5bb2-88b9-a0a57179b878"),
                column: "Name",
                value: "desiliter");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("780932a4-30a7-5208-8edc-b185679fc00c"),
                column: "Name",
                value: "teskje");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("bd92778d-4bcc-5c00-a80d-1bd1a08afb58"),
                column: "Name",
                value: "stykk");

            migrationBuilder.UpdateData(
                table: "Units",
                keyColumn: "Id",
                keyValue: new Guid("c7969cfb-2cf1-5d5f-9855-504b47ed4faf"),
                column: "Name",
                value: "fedd");
        }
    }
}
