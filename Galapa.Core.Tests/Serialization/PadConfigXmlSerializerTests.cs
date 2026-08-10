using System.Xml.Linq;
using Galapa.Core.Models;
using Galapa.Core.Serialization;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Serialization;

public class PadConfigXmlSerializerTests
{
	public static IEnumerable<object[]> LoadSamples()
	{
		yield return
		[
			"abxy1-xbox",
			@"<?xml version=""1.0"" encoding=""UTF-8""?>
<DragonQuestX>
	<PAD_CONFIG>
		<PAD_INFO>
			<param name=""PadGUIDInstance1"" value=""442652832""/>
			<param name=""PadGUIDInstance2"" value=""40077""/>
			<param name=""PadGUIDInstance3"" value=""4592""/>
			<param name=""PadGUIDInstance4"" value=""-2147138491""/>
			<param name=""PadGUIDInstance5"" value=""1398013952""/>
			<param name=""SpecialDecideType"" value=""0""/>
			<param name=""PadNonActive"" value=""1""/>
			<param name=""PadBias"" value=""50""/>
			<param name=""ButtonCaptionType"" value=""2""/>
			<param name=""PadPresetType"" value=""PadPresetTypeXBox""/>
		</PAD_INFO>
		<ACTION>
			<param name=""CONVENIENCE"" value=""0""/>
			<param name=""CANCEL"" value=""1""/>
			<param name=""AUTO_RUN"" value=""10""/>
			<param name=""JUMP"" value=""11""/>
			<param name=""CAMERA_AND_MODEL_ROT_CLOCKWISE"" value=""5""/>
			<param name=""CAMERA_AND_MODEL_ROT_ANTI_CLOCKWISE"" value=""4""/>
			<param name=""MENU"" value=""2""/>
			<param name=""MAP"" value=""3""/>
			<param name=""COMMUNICATION"" value=""7""/>
			<param name=""CAMERA_BEHIND"" value=""9""/>
			<param name=""CURSOR_UP"" value=""44""/>
			<param name=""CURSOR_DOWN"" value=""45""/>
			<param name=""CURSOR_LEFT"" value=""46""/>
			<param name=""CURSOR_RIGHT"" value=""47""/>
			<param name=""CAMERA_UP"" value=""42""/>
			<param name=""CAMERA_DOWN"" value=""43""/>
			<param name=""CAMERA_LEFT"" value=""36""/>
			<param name=""CAMERA_RIGHT"" value=""37""/>
			<param name=""MOVE_FORWARD"" value=""35""/>
			<param name=""MOVE_BACK"" value=""34""/>
			<param name=""MOVE_LEFT"" value=""33""/>
			<param name=""MOVE_RIGHT"" value=""32""/>
		</ACTION>
		<PadButtonCaption>
			<param name=""Button0"" value=""0""/>
			<param name=""Button1"" value=""1""/>
			<param name=""Button2"" value=""2""/>
			<param name=""Button3"" value=""3""/>
			<param name=""Button4"" value=""4""/>
			<param name=""Button5"" value=""5""/>
			<param name=""Button6"" value=""6""/>
			<param name=""Button7"" value=""7""/>
			<param name=""Button8"" value=""8""/>
			<param name=""Button9"" value=""9""/>
			<param name=""Button10"" value=""10""/>
			<param name=""Button11"" value=""11""/>
			<param name=""Button12"" value=""12""/>
			<param name=""Button13"" value=""13""/>
			<param name=""Button14"" value=""14""/>
			<param name=""Button15"" value=""15""/>
		</PadButtonCaption>
	</PAD_CONFIG>
</DragonQuestX>
",
			new PadConfig
			{
				PadInfo = new PadInfo
				{
					DeviceGuid = new Guid("1a6258a0-9c8d-11f0-8005-444553540000"),
					SpecialDecideType = 0,
					PadNonActive = 1,
					PadBias = 50,
					ButtonCaptionType = 2,
					PadPresetType = "PadPresetTypeXBox"
				},
				Action = new PadAction
				{
					Convenience = 0,
					Cancel = 1,
					AutoRun = 10,
					Jump = 11,
					CameraAndModelRotClockwise = 5,
					CameraAndModelRotAntiClockwise = 4,
					Menu = 2,
					Map = 3,
					Communication = 7,
					CameraBehind = 9,
					CursorUp = 44,
					CursorDown = 45,
					CursorLeft = 46,
					CursorRight = 47,
					CameraUp = 42,
					CameraDown = 43,
					CameraLeft = 36,
					CameraRight = 37,
					MoveForward = 35,
					MoveBack = 34,
					MoveLeft = 33,
					MoveRight = 32
				},
				ButtonCaption = new PadButtonCaption
				{
					Button0 = 0,
					Button1 = 1,
					Button2 = 2,
					Button3 = 3,
					Button4 = 4,
					Button5 = 5,
					Button6 = 6,
					Button7 = 7,
					Button8 = 8,
					Button9 = 9,
					Button10 = 10,
					Button11 = 11,
					Button12 = 12,
					Button13 = 13,
					Button14 = 14,
					Button15 = 15
				}
			}
		];
		yield return
		[
			"direct-input-preset",
			@"<?xml version=""1.0"" encoding=""UTF-8""?>
<DragonQuestX>
	<PAD_CONFIG>
		<PAD_INFO>
			<param name=""PadGUIDInstance1"" value=""442652832""/>
			<param name=""PadGUIDInstance2"" value=""40077""/>
			<param name=""PadGUIDInstance3"" value=""4592""/>
			<param name=""PadGUIDInstance4"" value=""-2147138491""/>
			<param name=""PadGUIDInstance5"" value=""1398013952""/>
			<param name=""SpecialDecideType"" value=""0""/>
			<param name=""PadNonActive"" value=""1""/>
			<param name=""PadBias"" value=""50""/>
			<param name=""ButtonCaptionType"" value=""0""/>
			<param name=""PadPresetType"" value=""PadPresetType16Button""/>
		</PAD_INFO>
		<ACTION>
			<param name=""CONVENIENCE"" value=""1""/>
			<param name=""CANCEL"" value=""3""/>
			<param name=""AUTO_RUN"" value=""6""/>
			<param name=""JUMP"" value=""7""/>
			<param name=""CAMERA_AND_MODEL_ROT_CLOCKWISE"" value=""9""/>
			<param name=""CAMERA_AND_MODEL_ROT_ANTI_CLOCKWISE"" value=""8""/>
			<param name=""MENU"" value=""0""/>
			<param name=""MAP"" value=""2""/>
			<param name=""COMMUNICATION"" value=""5""/>
			<param name=""CAMERA_BEHIND"" value=""11""/>
			<param name=""CURSOR_UP"" value=""44""/>
			<param name=""CURSOR_DOWN"" value=""45""/>
			<param name=""CURSOR_LEFT"" value=""46""/>
			<param name=""CURSOR_RIGHT"" value=""47""/>
			<param name=""CAMERA_UP"" value=""42""/>
			<param name=""CAMERA_DOWN"" value=""43""/>
			<param name=""CAMERA_LEFT"" value=""36""/>
			<param name=""CAMERA_RIGHT"" value=""37""/>
			<param name=""MOVE_FORWARD"" value=""35""/>
			<param name=""MOVE_BACK"" value=""34""/>
			<param name=""MOVE_LEFT"" value=""33""/>
			<param name=""MOVE_RIGHT"" value=""32""/>
		</ACTION>
		<PadButtonCaption>
			<param name=""Button0"" value=""0""/>
			<param name=""Button1"" value=""1""/>
			<param name=""Button2"" value=""2""/>
			<param name=""Button3"" value=""3""/>
			<param name=""Button4"" value=""4""/>
			<param name=""Button5"" value=""5""/>
			<param name=""Button6"" value=""6""/>
			<param name=""Button7"" value=""7""/>
			<param name=""Button8"" value=""8""/>
			<param name=""Button9"" value=""9""/>
			<param name=""Button10"" value=""10""/>
			<param name=""Button11"" value=""11""/>
			<param name=""Button12"" value=""12""/>
			<param name=""Button13"" value=""13""/>
			<param name=""Button14"" value=""14""/>
			<param name=""Button15"" value=""15""/>
		</PadButtonCaption>
	</PAD_CONFIG>
</DragonQuestX>
",
			new PadConfig
			{
				PadInfo = new PadInfo
				{
					DeviceGuid = new Guid("1a6258a0-9c8d-11f0-8005-444553540000"),
					SpecialDecideType = 0,
					PadNonActive = 1,
					PadBias = 50,
					ButtonCaptionType = 0,
					PadPresetType = "PadPresetType16Button"
				},
				Action = new PadAction
				{
					Convenience = 1,
					Cancel = 3,
					AutoRun = 6,
					Jump = 7,
					CameraAndModelRotClockwise = 9,
					CameraAndModelRotAntiClockwise = 8,
					Menu = 0,
					Map = 2,
					Communication = 5,
					CameraBehind = 11,
					CursorUp = 44,
					CursorDown = 45,
					CursorLeft = 46,
					CursorRight = 47,
					CameraUp = 42,
					CameraDown = 43,
					CameraLeft = 36,
					CameraRight = 37,
					MoveForward = 35,
					MoveBack = 34,
					MoveLeft = 33,
					MoveRight = 32
				},
				ButtonCaption = new PadButtonCaption
				{
					Button0 = 0,
					Button1 = 1,
					Button2 = 2,
					Button3 = 3,
					Button4 = 4,
					Button5 = 5,
					Button6 = 6,
					Button7 = 7,
					Button8 = 8,
					Button9 = 9,
					Button10 = 10,
					Button11 = 11,
					Button12 = 12,
					Button13 = 13,
					Button14 = 14,
					Button15 = 15
				}
			}
		];
	}

	[Theory]
	[MemberData(nameof(LoadSamples))]
	public void Load_KnownSamples_ParsesValues(
		string name,
		string xml,
		PadConfig padConfig)
	{
		using var tempDir = new TempDirectory();
		var path = Path.Combine(tempDir.Path, $"{name}.xml");
		File.WriteAllText(path, xml);

		var config = PadConfigXmlSerializer.Load(path);

		Assert.Equal(padConfig, config);
	}

	[Fact]
	public void Save_RoundTrip_PreservesValues()
	{
		var config = new PadConfig
		{
			PadInfo = new PadInfo
			{
				DeviceGuid = new Guid("1a6258a0-9c8d-11f0-8005-444553540000"),
				SpecialDecideType = 0,
				PadNonActive = 1,
				PadBias = 50,
				ButtonCaptionType = 2,
				PadPresetType = "PadPresetTypeXBox"
			},
			Action = new PadAction
			{
				Convenience = 0,
				Cancel = 1,
				AutoRun = 10,
				Jump = 11,
				CameraAndModelRotClockwise = 5,
				CameraAndModelRotAntiClockwise = 4,
				Menu = 2,
				Map = 3,
				Communication = 7,
				CameraBehind = 9,
				CursorUp = 44,
				CursorDown = 45,
				CursorLeft = 46,
				CursorRight = 47,
				CameraUp = 42,
				CameraDown = 43,
				CameraLeft = 36,
				CameraRight = 37,
				MoveForward = 35,
				MoveBack = 34,
				MoveLeft = 33,
				MoveRight = 32
			},
			ButtonCaption = new PadButtonCaption
			{
				Button0 = 0,
				Button1 = 1,
				Button2 = 2,
				Button3 = 3,
				Button4 = 4,
				Button5 = 5,
				Button6 = 6,
				Button7 = 7,
				Button8 = 8,
				Button9 = 9,
				Button10 = 10,
				Button11 = 11,
				Button12 = 12,
				Button13 = 13,
				Button14 = 14,
				Button15 = 15
			}
		};

		using var tempDir = new TempDirectory();
		var path = Path.Combine(tempDir.Path, "pad.xml");
		PadConfigXmlSerializer.Save(config, path);

		var reloaded = PadConfigXmlSerializer.Load(path);

		Assert.Equal(config, reloaded);
	}

	[Fact]
	public void Save_WritesGuidComponentsBigEndian()
	{
		var config = new PadConfig
		{
			PadInfo = new PadInfo
			{
				DeviceGuid = new Guid("1a6258a0-9c8d-11f0-8005-444553540000"),
				SpecialDecideType = 0,
				PadNonActive = 1,
				PadBias = 50,
				ButtonCaptionType = 2,
				PadPresetType = "PadPresetTypeXBox"
			},
			Action = new PadAction
			{
				Convenience = 0,
				Cancel = 1,
				AutoRun = 10,
				Jump = 11,
				CameraAndModelRotClockwise = 5,
				CameraAndModelRotAntiClockwise = 4,
				Menu = 2,
				Map = 3,
				Communication = 7,
				CameraBehind = 9,
				CursorUp = 44,
				CursorDown = 45,
				CursorLeft = 46,
				CursorRight = 47,
				CameraUp = 42,
				CameraDown = 43,
				CameraLeft = 36,
				CameraRight = 37,
				MoveForward = 35,
				MoveBack = 34,
				MoveLeft = 33,
				MoveRight = 32
			},
			ButtonCaption = new PadButtonCaption
			{
				Button0 = 0,
				Button1 = 1,
				Button2 = 2,
				Button3 = 3,
				Button4 = 4,
				Button5 = 5,
				Button6 = 6,
				Button7 = 7,
				Button8 = 8,
				Button9 = 9,
				Button10 = 10,
				Button11 = 11,
				Button12 = 12,
				Button13 = 13,
				Button14 = 14,
				Button15 = 15
			}
		};

		using var tempDir = new TempDirectory();
		var path = Path.Combine(tempDir.Path, "pad.xml");
		PadConfigXmlSerializer.Save(config, path);

		var doc = XDocument.Load(path);
		var padInfo = doc.Root?.Element("PAD_CONFIG")?.Element("PAD_INFO");
		Assert.NotNull(padInfo);

		Assert.Equal(442652832, ReadParamInt(padInfo, "PadGUIDInstance1"));
		Assert.Equal(40077, ReadParamInt(padInfo, "PadGUIDInstance2"));
		Assert.Equal(4592, ReadParamInt(padInfo, "PadGUIDInstance3"));
		Assert.Equal(-2147138491, ReadParamInt(padInfo, "PadGUIDInstance4"));
		Assert.Equal(1398013952, ReadParamInt(padInfo, "PadGUIDInstance5"));
	}


	private static int ReadParamInt(XElement parent, string name)
	{
		return int.Parse(parent.Elements("param")
			.First(e => e.Attribute("name")?.Value == name)
			.Attribute("value")?.Value ?? "0");
	}
}