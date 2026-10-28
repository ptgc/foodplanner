using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIngredientStoreSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Section",
                table: "Ingredients",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Other");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("00b577aa-2127-533c-8a68-f9099001f88a"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("018b5367-aabf-5218-9a80-387c1eefa30a"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("05a6160e-87cd-5d2c-81df-c0a753ba5e19"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("062621ee-301b-53d1-aa7b-0e00f1c33081"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("079d2db4-8e75-543b-84fe-5c82a7963559"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0942b134-c6e7-5b24-b9c7-0aa52f8783d6"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0c6bbe3a-b113-52a5-9fbe-c680dc6fa614"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0d7adfbb-5b54-5ae4-bd7e-ab5ea9de6b86"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0eb2fb2b-c95a-5044-99a3-d9453c52cc2d"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("0ede68df-ce20-58e2-a9fa-6cbeadf05b8b"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("1105b71c-7f8b-537d-a00c-11f2d21798a2"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("1127b196-6678-52fb-afe8-80ffd554e70c"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("12aaac43-57fb-55c3-979c-3a7cd2c71f22"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("12e6be7c-2862-509e-b602-b5bac890ca06"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("14edc321-5475-52f2-9b9c-a8e58631d691"),
                column: "Section",
                value: "Frozen");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("16bb8de3-129a-5b67-867e-969a2270c5e5"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("182bfd36-b47f-51af-b97d-c9c5e870636a"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("18aeb9c6-f737-5ae7-a050-a69952a33d65"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("1e775c0f-11de-56e3-9e48-162a8ce306b5"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("22fdd055-6217-5f93-93a4-e2c4fad857cb"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2437ea85-570e-5944-8af8-0a9572fae257"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("25530a97-c780-5914-b22c-c281940b37e1"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("28317d98-d5d6-521a-b011-a35b77fa4f55"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("28f29641-7731-5158-ad85-90ace26007a6"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2c102b4b-c557-59f1-ba38-fbb7edef5a2b"),
                column: "Section",
                value: "Frozen");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2da824bf-7d71-5906-8fd3-a2a54e547c69"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("2f80230b-4d31-56e2-b12f-f79776e21680"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("302edd54-59dc-5145-b469-c988cf1f3694"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("340165f7-81cb-5452-8e49-a5a4e02d3962"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("358bbaa7-68e5-5ebb-8bd7-adaa02707a1f"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("38796544-559e-5b16-833c-65eae0c8be7a"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("3cb77c8d-983a-5eef-a4fe-ac08cb08309b"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("481a68a8-b863-58be-87c5-0f2860de7f0e"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("48f5afed-5b6d-5e25-af57-48a47694d3f7"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("4cc2ada6-73b1-506f-818f-c5855d935d73"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("4e968abd-1dfe-579d-acf9-de5fdfeae671"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("55d1d54c-4791-500c-947d-11264953c044"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("578d165a-9764-56e3-8eee-7979def50aef"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("5a1af167-140c-561c-a977-8aefd9618c68"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("5a4a9fbe-71e7-55a1-bd65-e5a626e66c51"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("60cfdd20-06dd-509e-91d6-df0929a098c7"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("62629f8f-0cb8-5245-a091-6a7fe95b914b"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6593ae76-1a40-50b7-b118-976d50dd35a1"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6871d5c3-f983-5046-870b-095b26f4c9b3"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6968fff3-8d67-5bca-ac32-0caea672f02b"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("696b64ff-38fe-5660-b130-925b51a2e1f1"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6aca22ad-6eff-588f-b605-fb3d2c81df89"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6bbf5e98-4b0d-5818-9efc-c9fd3010a527"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6c2abe88-f414-54a9-8516-1bdc57f79b80"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6e596366-2e86-5b36-ae83-f0d5749150a1"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("6eb278e9-119d-5994-af30-45094f1072bd"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("722e16a9-d3ff-5004-9772-10933d4fe6bd"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("74a75ef6-eb74-5fe8-930a-6359f770bd74"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("78b635b5-8e1f-505f-a6dc-de67f8593ec1"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("79178d43-875e-52fa-83a7-33583762c88c"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("792a6751-38e0-5406-a86d-cf376c44014d"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("79b6b702-e4f9-5017-9a69-367c785fcaf8"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7bb12c8d-a801-55bd-b957-f04f33baddf2"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7c3ea56c-14ca-5a91-afdc-f568bf2a44f9"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7c5a3a77-7111-56d6-82ac-12d5fa52c10c"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("7d4f5a75-b5cb-5e1d-9db0-44d5f4e16256"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("80db0d1f-6512-58a7-a507-887a43683457"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("810748fa-acf5-538c-b92e-93de60765fb8"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("817f66e7-a5b1-57f8-9126-3047278e0740"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("81be2532-c29d-5f62-a1da-1985004d8435"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("86d85834-4e8c-5e1a-b079-556dde755928"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("906be377-84d2-5135-b6d5-4d95e9242255"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("91ea51af-b23d-525a-8f73-4b3a9b1579d7"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("9239cd66-0bdd-5d2f-90be-fdd746ebdc98"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("9455e9ad-ee5e-525c-81af-05f95ddaebcf"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("9889b811-51f0-5a78-911f-ddf64315f5dd"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("991a40d6-f019-532c-98ab-cf03a2ff1ef0"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("a17d723e-660b-5394-bacf-3f4a7f1dd119"),
                column: "Section",
                value: "MeatFishAndCheese");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("a994e67c-5ac7-5793-b00f-546bcc2a1822"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("aab25390-c788-518b-aaef-0cb573a5ba10"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ab733b3b-71d1-5d14-99a5-32905ee589ed"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ac11796b-2f44-52fb-8524-6fa2b3340a6c"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ae6ce0de-ba86-5cb2-a56f-a0c8449a0ca6"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b1c9c75b-ca8c-5b1d-931b-7ae58472b2f4"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b3daf0da-3cb7-5aec-bf47-ed04f352bec0"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b86a1379-8389-5a39-9081-14c0e331f2cd"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("b9f18d0b-f60a-5c28-9838-37fe57d7e2cc"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ba45e4fc-b4a3-55b3-b0b7-dcfb22358b0e"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("bedbb7cc-d514-5978-a105-6671fa93e8c3"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("bf538d98-0152-59f1-b4f2-7385fe43711a"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c223976c-0aec-54e8-b637-0bffe6cd80c5"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c253a6bb-412a-5308-8ba4-ff2ffba19662"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c2b92c25-7566-522e-b817-beedfe24fd49"),
                column: "Section",
                value: "Other");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("c66300ab-d1cc-516a-b548-7428baa2cef0"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("cc94a551-2fb1-5df4-ba10-27663092ade6"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("cef2dcdd-aab6-56b0-b4a7-cf69803006e2"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d25c1950-1087-560e-998d-498e10cf418e"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d2919ba2-38a0-5991-abb5-06a3a01ece4b"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d4e4de1c-5557-5d29-b2c7-ea8f29035f8a"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d8fa323d-76da-59c0-a895-1924aa8a3ca0"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("da5739f3-0171-54c0-834f-f0ea0632e189"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("da9bf5f7-9cdb-563d-91aa-38553c382aa6"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("dd4248ca-3750-5bf7-b7e7-610a6388f7cf"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("dff32bc5-07cd-59dd-9ba1-46f7e110ff57"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e0ba0668-a3ec-50ba-9063-115da67c2e6a"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e2221128-f4f3-5415-9ad2-2a3444660f18"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e387d7f6-3531-5fac-8ff5-1828c0a487f0"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("e995deeb-fa91-5698-ab94-caf43f7c4cc8"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ece38aeb-5105-5eef-b921-af6c0f1c3ce0"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("ed472bba-a9e2-5b5c-89f9-296472b8617b"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("f820ab7f-4d7f-51ec-a99b-53196dbdc901"),
                column: "Section",
                value: "DryGoods");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("f8986fd1-a524-5ff0-80e3-9795f0d5de2d"),
                column: "Section",
                value: "Dairy");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("f89cfb7c-b140-5315-be9b-31c60bb5f945"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("fa28a23a-4e65-50c5-b127-f3e6b1bd37d9"),
                column: "Section",
                value: "FruitAndVegetables");

            migrationBuilder.UpdateData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("fe6a6905-3590-54db-8bc7-5f9be03dffde"),
                column: "Section",
                value: "DryGoods");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Section",
                table: "Ingredients");
        }
    }
}
